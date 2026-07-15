import type { MetadataAttributeDto } from '../../../types.js';

/**
 * #1368: auxiliary attributes carry AttributeOf — the logical name of the attribute
 * they extend (lookup `…idname`/`…yominame` companions, image/virtual pairs).
 * Per "mark, don't mask" they are shown by default and visually marked; consumers
 * may offer a user-initiated hide. Absent field (bundled CLI older than the RPC
 * widening) ⇒ treated as a real attribute, matching pre-widening behavior.
 */
export function isAuxiliaryAttribute(a: MetadataAttributeDto): boolean {
    return typeof a.attributeOf === 'string' && a.attributeOf.length > 0;
}
