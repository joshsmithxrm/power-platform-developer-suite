using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Pooling;

namespace PPDS.Migration.Tests.Import;

/// <summary>
/// A recording fake of a target environment for plug-in step selection (#1921): tables by logical
/// name with the TARGET's object type codes, and active steps by the code of their table. Answers
/// RetrieveEntityRequest and the step FetchXML in pages of <see cref="PageSize"/> rows, and records
/// every step update with the state it set.
/// </summary>
internal sealed class FakePluginTarget
{
    public Dictionary<string, int> Codes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FakeStep> Steps { get; } = new();

    /// <summary>When set, the metadata response names this table instead of the one asked for.</summary>
    public string? MetadataAnswersWith { get; set; }
    public List<string> FetchXml { get; } = new();
    public List<string> MetadataRequests { get; } = new();
    public List<(Guid Id, int State)> StepUpdates { get; } = new();
    public int PageSize { get; set; } = 5000;

    /// <summary>Called before each step update is recorded; throw from it to fail that update.</summary>
    public Action<Guid, int>? OnUpdate { get; set; }

    /// <summary>The steps whose last recorded update left them disabled (state 1).</summary>
    public IEnumerable<Guid> LeftDisabled =>
        StepUpdates.GroupBy(u => u.Id).Where(g => g.Last().State == 1).Select(g => g.Key);

    public void Attach(Mock<IPooledClient> client)
    {
        client
            .Setup(x => x.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest request, CancellationToken _) =>
            {
                var retrieve = (RetrieveEntityRequest)request;
                MetadataRequests.Add(retrieve.LogicalName);
                if (!Codes.TryGetValue(retrieve.LogicalName, out var code))
                {
                    throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                        new OrganizationServiceFault { Message = "Could not find entity" });
                }

                var metadata = new EntityMetadata { LogicalName = MetadataAnswersWith ?? retrieve.LogicalName };
                typeof(EntityMetadata).GetProperty(nameof(EntityMetadata.ObjectTypeCode))!.SetValue(metadata, code);
                var response = new RetrieveEntityResponse();
                response.Results["EntityMetadata"] = metadata;
                return response;
            });

        client
            .Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase query, CancellationToken _) =>
            {
                var xml = ((FetchExpression)query).Query;
                FetchXml.Add(xml);
                var doc = System.Xml.Linq.XDocument.Parse(xml);
                var conditions = doc.Descendants("condition")
                    .Select(c => ((string)c.Attribute("attribute")!, (string)c.Attribute("value")!))
                    .ToList();
                var codes = conditions.Where(c => c.Item1 == "primaryobjecttypecode").Select(c => int.Parse(c.Item2)).ToHashSet();
                // Each step condition is applied only when the query sends it, so a query that
                // drops one selects the steps it should not (#1921 review MF5).
                bool Holds(FakeStep st, string attribute, int value) => !conditions.Any(c => c.Item1 == attribute) ||
                    conditions.Any(c => c.Item1 == attribute && int.Parse(c.Item2) == value);
                var page = int.Parse((string?)doc.Root!.Attribute("page") ?? "1");
                var matching = Steps.Where(st => codes.Contains(st.Code)
                        && Holds(st, "statecode", st.Enabled ? 0 : 1)
                        && Holds(st, "ishidden", st.Hidden ? 1 : 0)
                        && Holds(st, "customizationlevel", st.CustomizationLevel))
                    .ToList();
                var rows = matching.Skip((page - 1) * PageSize).Take(PageSize)
                    .Select(st => new Entity("sdkmessageprocessingstep", st.Id) { ["name"] = st.Name })
                    .ToList();
                return new EntityCollection(rows)
                {
                    MoreRecords = page * PageSize < matching.Count,
                    PagingCookie = $"<cookie page=\"{page}\" />"
                };
            });

        client
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns((Entity entity, CancellationToken _) =>
            {
                var state = entity.GetAttributeValue<OptionSetValue>("statecode").Value;
                OnUpdate?.Invoke(entity.Id, state);
                StepUpdates.Add((entity.Id, state));
                var step = Steps.FirstOrDefault(st => st.Id == entity.Id);
                if (step != null) step.Enabled = state == 0;
                return Task.CompletedTask;
            });
    }
}

/// <summary>A step in the fake target. Enabled, not hidden and custom (level 1) unless set otherwise.</summary>
internal sealed class FakeStep
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int Code { get; init; }
    public bool Enabled { get; set; } = true;
    public bool Hidden { get; init; }
    public int CustomizationLevel { get; init; } = 1;

    public static implicit operator FakeStep((Guid Id, string Name, int Code) step) =>
        new() { Id = step.Id, Name = step.Name, Code = step.Code };
}
