#!/usr/bin/env python3
"""Shared release version and package-impact model for PPDS.

This module is intentionally read-only.  It discovers publishable and
build-only .NET projects, their dependency graph, and declarative package
inputs from MSBuild XML, adds non-MSBuild deliverables from
``release_surfaces.json``, and produces an explained release *advisory*. It
never creates or pushes tags and never invokes a publishing workflow.
"""
from __future__ import annotations

import json
import re
import subprocess
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from functools import total_ordering
from pathlib import Path, PurePosixPath
from typing import Iterable, Optional, Sequence


_SEMVER_RE = re.compile(
    r"^(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)"
    r"(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)


@total_ordering
@dataclass(frozen=True)
class SemVer:
    """Strict SemVer 2.0 value with precedence-aware comparison.

    Build metadata is retained for display but ignored for precedence and
    equality, as required by SemVer 2.0 section 10.
    """

    major: int
    minor: int
    patch: int
    prerelease: tuple[str, ...] = ()
    build: tuple[str, ...] = ()

    @classmethod
    def parse(cls, value: str) -> "SemVer":
        match = _SEMVER_RE.fullmatch(value)
        if not match:
            raise ValueError(
                f"{value!r} is not a valid SemVer 2.0 version "
                "(expected MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD])"
            )
        prerelease = tuple(match.group(4).split(".")) if match.group(4) else ()
        build = tuple(match.group(5).split(".")) if match.group(5) else ()
        return cls(
            major=int(match.group(1)),
            minor=int(match.group(2)),
            patch=int(match.group(3)),
            prerelease=prerelease,
            build=build,
        )

    @property
    def is_prerelease(self) -> bool:
        return bool(self.prerelease)

    def compare_precedence(self, other: "SemVer") -> int:
        if not isinstance(other, SemVer):
            raise TypeError(f"cannot compare SemVer with {type(other).__name__}")

        own_core = (self.major, self.minor, self.patch)
        other_core = (other.major, other.minor, other.patch)
        if own_core != other_core:
            return -1 if own_core < other_core else 1

        if not self.prerelease and not other.prerelease:
            return 0
        if not self.prerelease:
            return 1
        if not other.prerelease:
            return -1

        for own_part, other_part in zip(self.prerelease, other.prerelease):
            if own_part == other_part:
                continue
            own_numeric = own_part.isascii() and own_part.isdigit()
            other_numeric = other_part.isascii() and other_part.isdigit()
            if own_numeric and other_numeric:
                return -1 if int(own_part) < int(other_part) else 1
            if own_numeric != other_numeric:
                return -1 if own_numeric else 1
            return -1 if own_part < other_part else 1

        if len(self.prerelease) == len(other.prerelease):
            return 0
        return -1 if len(self.prerelease) < len(other.prerelease) else 1

    def __eq__(self, other: object) -> bool:
        return isinstance(other, SemVer) and self.compare_precedence(other) == 0

    def __lt__(self, other: "SemVer") -> bool:
        if not isinstance(other, SemVer):
            return NotImplemented
        return self.compare_precedence(other) < 0

    def __hash__(self) -> int:
        # Build metadata does not participate in SemVer precedence/equality.
        return hash((self.major, self.minor, self.patch, self.prerelease))

    def __str__(self) -> str:
        value = f"{self.major}.{self.minor}.{self.patch}"
        if self.prerelease:
            value += "-" + ".".join(self.prerelease)
        if self.build:
            value += "+" + ".".join(self.build)
        return value


@dataclass(frozen=True)
class TagSelection:
    latest: Optional[str]
    diagnostics: tuple[str, ...] = ()


def select_latest_tag(tags: Iterable[str], tag_prefix: str) -> TagSelection:
    """Select the highest valid SemVer tag for *tag_prefix*.

    Malformed tags with the requested prefix are ignored but returned as clear
    diagnostics.  Tags for other release surfaces are unrelated and ignored.
    """

    latest_name: Optional[str] = None
    latest_version: Optional[SemVer] = None
    diagnostics: list[str] = []

    for tag in tags:
        if not tag.startswith(tag_prefix):
            continue
        raw_version = tag[len(tag_prefix):]
        try:
            version = SemVer.parse(raw_version)
        except ValueError as exc:
            diagnostics.append(f"Malformed release tag {tag!r}: {exc}")
            continue

        comparison = 1 if latest_version is None else version.compare_precedence(latest_version)
        # Build metadata has no precedence.  Use the full tag only as a stable
        # tie-break so results do not depend on git/ref enumeration order.
        if comparison > 0 or (
            comparison == 0 and latest_name is not None and tag > latest_name
        ):
            latest_name = tag
            latest_version = version

    return TagSelection(latest=latest_name, diagnostics=tuple(sorted(diagnostics)))


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _first_element_text(root: ET.Element, name: str) -> Optional[str]:
    for element in root.iter():
        if _local_name(element.tag) == name and element.text and element.text.strip():
            return element.text.strip()
    return None


def _normalise_repo_path(path: str | Path) -> str:
    value = str(path).replace("\\", "/")
    if value.startswith("./"):
        value = value[2:]
    return str(PurePosixPath(value))


def _xml_root(content: Optional[str]) -> Optional[ET.Element]:
    if content is None:
        return None
    try:
        return ET.fromstring(content)
    except ET.ParseError:
        return None


def _canonical_xml(content: Optional[str]) -> Optional[tuple]:
    root = _xml_root(content)
    if root is None:
        return None

    def content_or_none(value: Optional[str]) -> Optional[str]:
        # Whitespace-only text between MSBuild elements is formatting. Any
        # non-whitespace text is potentially an MSBuild value or task input and
        # must remain byte-for-byte significant (including repeated spaces).
        if value is None or not value.strip():
            return None
        return value

    def canonical(element: ET.Element) -> tuple:
        # ElementTree retains namespace URIs in Clark notation for element and
        # attribute names. Do not collapse to local names: changing an xmlns is
        # a semantic change. Attribute values are also exact because task
        # parameters such as Exec.Command can be whitespace-sensitive.
        attributes = tuple(sorted(element.attrib.items()))
        text = content_or_none(element.text)
        tail = content_or_none(element.tail)
        children = tuple(canonical(child) for child in list(element))
        return (element.tag, attributes, text, tail, children)

    return canonical(root)


def _package_versions(content: Optional[str]) -> Optional[dict[str, str]]:
    root = _xml_root(content)
    if root is None:
        return None
    result: dict[str, str] = {}
    for element in root.iter():
        if _local_name(element.tag) != "PackageVersion":
            continue
        package = element.attrib.get("Include") or element.attrib.get("Update")
        if not package:
            continue
        version = element.attrib.get("Version")
        if version is None:
            for child in element:
                if _local_name(child.tag) == "Version":
                    version = (child.text or "").strip()
                    break
        # NuGet package identifiers are case-insensitive. Normalising at the
        # boundary makes central version comparisons and consumer matching
        # obey the same identity rules.
        result[package.casefold()] = version or ""
    return result


def _central_package_residual_semantics(content: Optional[str]) -> Optional[tuple]:
    """Return central-package XML after removing simple version declarations.

    ``PackageVersion`` entries whose only semantics are package identity and
    version are classified separately by :func:`_package_versions`. Everything
    else remains in this representation so a version bump cannot hide a
    simultaneous repository-wide central package-management change.
    """

    root = _xml_root(content)
    if root is None:
        return None

    def content_or_none(value: Optional[str]) -> Optional[str]:
        if value is None or not value.strip():
            return None
        return value

    def is_simple_package_version(element: ET.Element) -> bool:
        if _local_name(element.tag) != "PackageVersion":
            return False
        if content_or_none(element.text) is not None or content_or_none(element.tail) is not None:
            return False
        if not set(element.attrib) <= {"Include", "Update", "Version"}:
            return False
        if ("Include" in element.attrib) == ("Update" in element.attrib):
            return False

        children = list(element)
        if "Version" in element.attrib:
            return not children
        if len(children) != 1:
            return False
        version = children[0]
        return (
            _local_name(version.tag) == "Version"
            and not version.attrib
            and not list(version)
            and content_or_none(version.text) is not None
            and content_or_none(version.tail) is None
        )

    def canonical(element: ET.Element) -> tuple:
        attributes = tuple(sorted(element.attrib.items()))
        text = content_or_none(element.text)
        tail = content_or_none(element.tail)
        children = tuple(
            canonical(child)
            for child in list(element)
            if not is_simple_package_version(child)
        )
        return (element.tag, attributes, text, tail, children)

    return canonical(root)


@dataclass(frozen=True)
class Surface:
    name: str
    root: str
    tag_prefix: str
    project_path: Optional[str]
    project_dependencies: frozenset[str] = frozenset()
    package_references: frozenset[str] = frozenset()
    package_inputs: frozenset[str] = frozenset()
    unresolved_package_inputs: frozenset[str] = frozenset()
    bundles: frozenset[str] = frozenset()
    is_tool: bool = False

    @property
    def dependencies(self) -> frozenset[str]:
        return self.project_dependencies | self.bundles


@dataclass(frozen=True)
class ReleaseGraph:
    surfaces: dict[str, Surface]
    build_nodes: dict[str, Surface] = field(default_factory=dict)

    @property
    def nodes(self) -> dict[str, Surface]:
        """All build nodes, while ``surfaces`` remains release-target only."""
        return {**self.build_nodes, **self.surfaces}

    @classmethod
    def discover(
        cls,
        repo_root: Path,
        *,
        delivery_manifest_path: Optional[Path] = None,
    ) -> "ReleaseGraph":
        repo_root = repo_root.resolve()
        discovered: dict[str, dict] = {}
        project_to_node: dict[str, str] = {}

        for project in sorted((repo_root / "src").glob("PPDS.*/*.csproj")):
            try:
                root = ET.parse(project).getroot()
            except ET.ParseError as exc:
                raise ValueError(f"Cannot parse MSBuild project {project}: {exc}") from exc

            tag_prefix = _first_element_text(root, "MinVerTagPrefix")
            package_id = _first_element_text(root, "PackageId")
            project_path = _normalise_repo_path(project.relative_to(repo_root))
            node_name = package_id or project.stem
            project_to_node[project_path.casefold()] = node_name
            package_references: set[str] = set()
            project_references: list[str] = []
            package_inputs: set[str] = set()
            unresolved_package_inputs: set[str] = set()
            for element in root.iter():
                name = _local_name(element.tag)
                if name == "PackageReference":
                    package = element.attrib.get("Include") or element.attrib.get("Update")
                    if package:
                        package_references.add(package)
                elif name == "ProjectReference":
                    include = element.attrib.get("Include")
                    if include:
                        # ReferenceOutputAssembly=false still participates in
                        # compilation for analyzer/build-asset references.
                        project_references.append(include)

                if name in {"PackageReference", "ProjectReference"}:
                    continue

                include = element.attrib.get("Include") or element.attrib.get("Update")
                pack = element.attrib.get("Pack")
                if pack is None:
                    pack = next(
                        (
                            child.text
                            for child in element
                            if _local_name(child.tag) == "Pack"
                        ),
                        None,
                    )
                if not include or (pack or "").strip().casefold() != "true":
                    continue
                for declared_input in include.split(";"):
                    declared_input = declared_input.strip()
                    if not declared_input:
                        continue
                    if any(token in declared_input for token in ("$(", "@(", "%(", "*", "?")):
                        unresolved_package_inputs.add(declared_input)
                        continue
                    try:
                        input_path = (
                            project.parent / declared_input.replace("\\", "/")
                        ).resolve().relative_to(repo_root)
                    except (OSError, ValueError):
                        unresolved_package_inputs.add(declared_input)
                    else:
                        package_inputs.add(_normalise_repo_path(input_path))

            discovered[node_name] = {
                "name": node_name,
                "root": _normalise_repo_path(project.parent.relative_to(repo_root)),
                "tag_prefix": tag_prefix or "",
                "project_path": project_path,
                "raw_project_references": project_references,
                "package_references": frozenset(package_references),
                "package_inputs": frozenset(package_inputs),
                "unresolved_package_inputs": frozenset(unresolved_package_inputs),
                "is_tool": (_first_element_text(root, "PackAsTool") or "false").casefold() == "true",
                "is_release_surface": bool(tag_prefix and package_id),
            }

        surfaces: dict[str, Surface] = {}
        build_nodes: dict[str, Surface] = {}
        for name, values in discovered.items():
            project_dir = (repo_root / values["project_path"]).parent
            dependencies: set[str] = set()
            for include in values.pop("raw_project_references"):
                try:
                    dependency_path = _normalise_repo_path(
                        (project_dir / include.replace("\\", "/"))
                        .resolve()
                        .relative_to(repo_root)
                    )
                except (OSError, ValueError) as exc:
                    raise ValueError(
                        f"ProjectReference {include!r} in {values['project_path']} "
                        "cannot be mapped inside the repository"
                    ) from exc
                dependency = project_to_node.get(dependency_path.casefold())
                if not dependency:
                    raise ValueError(
                        f"ProjectReference {include!r} in {values['project_path']} "
                        f"targets undiscovered project {dependency_path}"
                    )
                dependencies.add(dependency)
            is_release_surface = values.pop("is_release_surface")
            node = Surface(
                **values,
                project_dependencies=frozenset(dependencies),
            )
            if is_release_surface:
                surfaces[name] = node
            else:
                build_nodes[name] = node

        manifest_path = delivery_manifest_path or (
            repo_root / "scripts" / "ci" / "release_surfaces.json"
        )
        if manifest_path.exists():
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            for item in manifest.get("deliverables", []):
                bundles: set[str] = set()
                for project_path in item.get("bundlesProjects", []):
                    dependency = project_to_node.get(_normalise_repo_path(project_path).casefold())
                    if not dependency:
                        raise ValueError(
                            f"Delivery surface {item['name']} references unknown project {project_path}"
                        )
                    bundles.add(dependency)
                surfaces[item["name"]] = Surface(
                    name=item["name"],
                    root=_normalise_repo_path(item["root"]),
                    tag_prefix=item["tagPrefix"],
                    project_path=None,
                    bundles=frozenset(bundles),
                )

        if not surfaces:
            raise ValueError(f"No release surfaces discovered below {repo_root / 'src'}")
        return cls(surfaces=surfaces, build_nodes=build_nodes)

    @property
    def dotnet_surfaces(self) -> frozenset[str]:
        return frozenset(name for name, surface in self.surfaces.items() if surface.project_path)

    def downstream_of(self, sources: Iterable[str]) -> dict[str, set[str]]:
        """Return every transitive consumer and the source(s) that reach it."""
        source_set = set(sources)
        # Seed each direct source with its own identity.  Whenever a node gains
        # another source reason, revisit it so that the larger set propagates to
        # every transitive consumer.  The sets only grow from the finite
        # ``source_set``, so cycles converge without losing direct identities.
        reached_by: dict[str, set[str]] = {
            source: {source} for source in source_set
        }
        frontier = sorted(source_set)
        while frontier:
            dependency = frontier.pop(0)
            inherited = reached_by[dependency]
            for name, surface in self.nodes.items():
                if dependency not in surface.dependencies:
                    continue
                reasons = reached_by.setdefault(name, set())
                before = len(reasons)
                reasons.update(inherited)
                if len(reasons) != before:
                    frontier.append(name)
        for source in source_set:
            reached_by.pop(source, None)
        return {
            name: sources
            for name, sources in reached_by.items()
            if name in self.surfaces
        }


@dataclass(frozen=True)
class FileChange:
    path: str
    before: Optional[str] = None
    after: Optional[str] = None


@dataclass
class _ImpactAccumulator:
    direct_reasons: dict[str, list[str]] = field(default_factory=dict)
    internal_reasons: dict[str, list[str]] = field(default_factory=dict)
    ignored: list[dict[str, str]] = field(default_factory=list)
    diagnostics: list[str] = field(default_factory=list)

    def direct(self, surface: str, reason: str) -> None:
        self.direct_reasons.setdefault(surface, []).append(reason)

    def internal(self, node: str, reason: str) -> None:
        self.internal_reasons.setdefault(node, []).append(reason)

    def ignore(self, path: str, reason: str) -> None:
        self.ignored.append({"path": path, "reason": reason})


def _is_deterministic_non_product(path: str) -> Optional[str]:
    pure = PurePosixPath(path)
    parts = tuple(part.casefold() for part in pure.parts)
    filename = pure.name.casefold()

    if parts and parts[0] in {"docs", "specs", "tests"}:
        return f"{parts[0]} change"
    if parts and parts[0] in {".github", ".claude", ".codex"}:
        return "automation/tooling change"
    if filename == "changelog.md":
        return "changelog-only change"
    if filename.endswith((".md", ".mdx")):
        return "documentation change"
    if any(part in {"__tests__", "tests", "test", "e2e", "fixtures"} for part in parts):
        return "test/fixture change"
    if any(marker in filename for marker in (".test.", ".tests.", ".spec.")):
        return "test change"
    return None


def _content_is_comment_only(change: FileChange) -> bool:
    suffix = PurePosixPath(change.path).suffix.casefold()
    # Comment suppression is deliberately limited to modeled MSBuild XML.
    # Treat C# and arbitrary XML as product content: proving comments outside
    # strings/raw strings or mixed-content text requires a language lexer.
    if suffix in {".props", ".targets", ".csproj"}:
        before = _canonical_xml(change.before)
        after = _canonical_xml(change.after)
        return before is not None and after is not None and before == after
    return False


def _detect_direct_changes(graph: ReleaseGraph, changes: Sequence[FileChange]) -> _ImpactAccumulator:
    result = _ImpactAccumulator()
    roots = sorted(
        ((node.root.rstrip("/") + "/", name) for name, node in graph.nodes.items()),
        key=lambda item: len(item[0]),
        reverse=True,
    )
    package_consumers: dict[str, set[str]] = {}
    for surface_name, surface in graph.surfaces.items():
        for package_input in surface.package_inputs:
            package_consumers.setdefault(package_input.casefold(), set()).add(surface_name)
    unresolved_package_consumers = {
        surface_name: surface.unresolved_package_inputs
        for surface_name, surface in graph.surfaces.items()
        if surface.unresolved_package_inputs
    }

    for raw_change in changes:
        change = FileChange(
            path=_normalise_repo_path(raw_change.path),
            before=raw_change.before,
            after=raw_change.after,
        )
        path_key = change.path.casefold()
        packed_by = package_consumers.get(path_key, set())
        if packed_by:
            for surface_name in sorted(packed_by):
                result.direct(
                    surface_name,
                    f"packed package asset changed: {change.path}",
                )
            continue
        if _content_is_comment_only(change):
            result.ignore(change.path, "semantic content unchanged after comments/XML docs were removed")
            continue

        if path_key == "directory.packages.props":
            before_versions = _package_versions(change.before)
            after_versions = _package_versions(change.after)
            before_residual = _central_package_residual_semantics(change.before)
            after_residual = _central_package_residual_semantics(change.after)
            if (
                before_versions is None
                or after_versions is None
                or before_residual is None
                or after_residual is None
            ):
                result.diagnostics.append(
                    "Directory.Packages.props could not be parsed on both sides; all .NET surfaces were conservatively included"
                )
                for surface in graph.dotnet_surfaces:
                    result.direct(surface, "shared central package input changed and could not be classified")
                continue

            changed_packages = sorted(
                package
                for package in set(before_versions) | set(after_versions)
                if before_versions.get(package) != after_versions.get(package)
            )
            residual_changed = before_residual != after_residual
            if residual_changed:
                for surface in graph.dotnet_surfaces:
                    result.direct(surface, "shared central package-management settings changed")

            if not changed_packages:
                if not residual_changed:
                    # The full XML changed but neither the version map nor the
                    # modeled residual explains it (for example, declaration
                    # ordering). Treat that uncertainty as repository-wide.
                    for surface in graph.dotnet_surfaces:
                        result.direct(
                            surface,
                            "central package declarations changed and could not be classified safely",
                        )
                continue
            matched = False
            for node_name, node in graph.nodes.items():
                used = sorted(
                    package
                    for package in node.package_references
                    if package.casefold() in changed_packages
                )
                if used:
                    matched = True
                    reason = f"central dependency changed: {', '.join(used)}"
                    if node_name in graph.surfaces:
                        result.direct(node_name, reason)
                    else:
                        result.internal(node_name, reason)
            if not matched:
                result.diagnostics.append(
                    "Central package version changed but no project references it directly: "
                    + ", ".join(changed_packages)
                )
            continue

        shared_dotnet_inputs = {
            "directory.build.props",
            "directory.build.targets",
            "global.json",
            "nuget.config",
            ".editorconfig",
        }
        if path_key in shared_dotnet_inputs:
            for surface in graph.dotnet_surfaces:
                result.direct(surface, f"repository-wide .NET build input changed: {change.path}")
            continue

        # An MSBuild expression cannot be safely resolved without evaluation.
        # Known central/shared inputs have already been handled above; for any
        # remaining change, fail conservatively for release projects that have
        # an unresolved declarative Pack input.
        if unresolved_package_consumers:
            result.diagnostics.append(
                f"Change {change.path!r} could not be excluded from declarative Pack inputs; "
                "packages with unresolved Pack expressions were conservatively included"
            )
            for surface_name, declarations in sorted(unresolved_package_consumers.items()):
                result.direct(
                    surface_name,
                    "could match unresolved Pack input(s): " + ", ".join(sorted(declarations)),
                )
            continue

        ignored_reason = _is_deterministic_non_product(change.path)
        if ignored_reason:
            result.ignore(change.path, ignored_reason)
            continue

        direct_node = next(
            (name for root, name in roots if change.path.startswith(root)),
            None,
        )
        if direct_node:
            if change.before is None or change.after is None:
                detail = "source added/deleted or content unavailable; included conservatively"
            else:
                detail = "runtime/package source changed"
            reason = f"{change.path}: {detail}"
            if direct_node in graph.surfaces:
                result.direct(direct_node, reason)
            else:
                result.internal(direct_node, reason)
            continue

        if change.path.startswith("src/"):
            result.diagnostics.append(
                f"Unmapped source change {change.path!r}; all .NET surfaces were conservatively included"
            )
            for surface in graph.dotnet_surfaces:
                result.direct(
                    surface,
                    f"unmapped source input changed: {change.path}; included conservatively",
                )

    return result


def _reason_entries(reasons: dict[str, list[str]]) -> list[dict[str, object]]:
    return [
        {"surface": surface, "reasons": sorted(set(surface_reasons))}
        for surface, surface_reasons in sorted(reasons.items())
    ]


def build_release_plan(
    graph: ReleaseGraph,
    changes: Sequence[FileChange],
    *,
    release_kind: str = "patch",
    channel: str = "stable",
    tags: Iterable[str] = (),
) -> dict:
    """Build an explained, read-only release plan.

    ``release_kind`` is ``patch``, ``minor``, or ``major``.  Minor and major
    releases are coordinated and therefore plan every discovered surface.
    Stable library releases also list missing upstream ProjectReferences as
    same-commit MinVer tag prerequisites; tool packages bundle their project
    outputs and do not expose those references as NuGet dependencies.
    """

    if release_kind not in {"patch", "minor", "major"}:
        raise ValueError("release_kind must be patch, minor, or major")
    if channel not in {"stable", "prerelease"}:
        raise ValueError("channel must be stable or prerelease")

    impact = _detect_direct_changes(graph, changes)
    direct_names = set(impact.direct_reasons)
    internal_names = set(impact.internal_reasons)
    downstream_sources = graph.downstream_of(direct_names | internal_names)
    downstream_reasons: dict[str, list[str]] = {}
    for surface, sources in downstream_sources.items():
        release_sources = sorted(sources & direct_names)
        internal_sources = sorted(sources & internal_names)
        reasons: list[str] = []
        if release_sources:
            reasons.append("consumes changed surface(s): " + ", ".join(release_sources))
        if internal_sources:
            reasons.append(
                "compiled with changed internal build node(s): "
                + ", ".join(internal_sources)
            )
        downstream_reasons[surface] = reasons
    affected = direct_names | set(downstream_reasons)

    if release_kind in {"minor", "major"}:
        release_targets = set(graph.surfaces)
        scope_explanation = (
            f"Coordinated {release_kind} releases plan every release surface, "
            "including surfaces with no direct user-facing change."
        )
    else:
        release_targets = set(affected)
        scope_explanation = (
            "Patch targets are the direct product changes plus downstream "
            "deliverables that consume those changes."
        )

    delivery_prerequisites: dict[str, set[str]] = {}
    for target in sorted(release_targets):
        for dependency in graph.surfaces[target].bundles:
            if dependency in graph.surfaces and dependency not in release_targets:
                delivery_prerequisites.setdefault(dependency, set()).add(target)

    prerequisites: dict[str, set[str]] = {}
    if channel == "stable":
        for target in sorted(release_targets):
            surface = graph.surfaces[target]
            if surface.is_tool or not surface.project_path:
                continue
            frontier = list(surface.project_dependencies)
            visited: set[str] = set()
            while frontier:
                dependency = frontier.pop(0)
                if dependency in visited:
                    continue
                visited.add(dependency)
                dependency_node = graph.nodes[dependency]
                if dependency in graph.surfaces and dependency not in release_targets:
                    prerequisites.setdefault(dependency, set()).add(target)
                frontier.extend(dependency_node.project_dependencies)

    tag_list = list(tags)
    latest_tags: dict[str, Optional[str]] = {}
    diagnostics = list(impact.diagnostics)
    for name, surface in sorted(graph.surfaces.items()):
        selection = select_latest_tag(tag_list, surface.tag_prefix)
        latest_tags[name] = selection.latest
        diagnostics.extend(selection.diagnostics)

    prerequisite_entries = [
        {
            "surface": surface,
            "required_by": sorted(required_by),
            "reason": (
                "Stable NuGet packing requires this ProjectReference to have a "
                "stable MinVer tag on the same commit; otherwise MinVer derives "
                "an alpha version."
            ),
        }
        for surface, required_by in sorted(prerequisites.items())
    ]
    delivery_prerequisite_entries = [
        {
            "surface": surface,
            "required_by": sorted(required_by),
            "reason": (
                "The publisher resolves this bundled deliverable from its "
                "release tag on the same commit; without that tag the publish "
                "stops before bundling."
            ),
        }
        for surface, required_by in sorted(delivery_prerequisites.items())
    ]

    return {
        "mode": "advisory",
        "release_kind": release_kind,
        "channel": channel,
        "release_needed": bool(release_targets),
        "direct_product_changes": _reason_entries(impact.direct_reasons),
        "internal_build_changes": _reason_entries(impact.internal_reasons),
        "downstream_deliverables": _reason_entries(downstream_reasons),
        "affected_surfaces": sorted(affected),
        "release_targets": sorted(release_targets),
        "release_scope_explanation": scope_explanation,
        "minver_tag_prerequisites": prerequisite_entries,
        "delivery_tag_prerequisites": delivery_prerequisite_entries,
        "latest_tags": latest_tags,
        "ignored_changes": sorted(impact.ignored, key=lambda item: item["path"]),
        "diagnostics": sorted(set(diagnostics)),
        "safety": "This plan is advisory only; it does not tag, publish, or dispatch workflows.",
    }


def collect_git_tags(repo_root: Path) -> list[str]:
    result = subprocess.run(
        ["git", "tag", "--list"],
        cwd=repo_root,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    if result.returncode != 0:
        raise RuntimeError(f"git tag --list failed: {result.stderr.strip()}")
    return [line.strip() for line in result.stdout.splitlines() if line.strip()]


def render_markdown(plan: dict) -> str:
    """Render the explained plan for a GitHub issue or release review."""

    lines = [
        "## Release Scope Advisory",
        "",
        "> [!IMPORTANT]",
        "> This analysis is advisory only. It does not create tags, publish packages, or dispatch release workflows.",
        "",
        f"**Plan:** {plan['channel']} {plan['release_kind']} — "
        + ("release work identified" if plan["release_needed"] else "no product release identified"),
        "",
        "### Direct Product Changes",
        "",
    ]

    def add_reason_entries(entries: list[dict], empty: str) -> None:
        if not entries:
            lines.append(f"- {empty}")
            return
        for entry in entries:
            lines.append(f"- **{entry['surface']}** — {'; '.join(entry['reasons'])}")

    add_reason_entries(plan["direct_product_changes"], "None.")
    lines.extend(["", "### Internal Build Changes", ""])
    add_reason_entries(
        plan["internal_build_changes"],
        "None. No build-only project changed.",
    )
    lines.extend(["", "### Downstream Deliverables", ""])
    add_reason_entries(
        plan["downstream_deliverables"],
        "None. No deliverable consumes a directly changed product surface.",
    )

    lines.extend(["", "### Same-Commit Delivery Tag Prerequisites", ""])
    if plan["delivery_tag_prerequisites"]:
        for entry in plan["delivery_tag_prerequisites"]:
            required_by = ", ".join(entry["required_by"])
            lines.append(f"- **{entry['surface']}** — required by {required_by}. {entry['reason']}")
    else:
        lines.append("- None.")

    lines.extend(["", "### Same-Commit MinVer Tag Prerequisites", ""])
    if plan["minver_tag_prerequisites"]:
        for entry in plan["minver_tag_prerequisites"]:
            required_by = ", ".join(entry["required_by"])
            lines.append(f"- **{entry['surface']}** — required by {required_by}. {entry['reason']}")
    else:
        lines.append("- None.")

    lines.extend([
        "",
        "### Advisory Release Targets",
        "",
        f"{plan['release_scope_explanation']}",
        "",
    ])
    if plan["release_targets"]:
        lines.extend(f"- {surface}" for surface in plan["release_targets"])
    else:
        lines.append("- None.")

    lines.extend(["", "### Latest Valid Release Tags", ""])
    for surface, tag in plan["latest_tags"].items():
        lines.append(f"- **{surface}** — `{tag or '(no valid tag found)'}`")

    if plan["ignored_changes"]:
        lines.extend(["", "### Ignored Non-Product Changes", ""])
        for item in plan["ignored_changes"]:
            lines.append(f"- `{item['path']}` — {item['reason']}")

    if plan["diagnostics"]:
        lines.extend(["", "### Diagnostics Requiring Review", ""])
        lines.extend(f"- {diagnostic}" for diagnostic in plan["diagnostics"])

    return "\n".join(lines) + "\n"
