/**
 * Domain Service: FetchXML to SQL Transpiler
 *
 * Converts FetchXML to SQL for display and editing.
 * Provides reverse transpilation with warnings for unsupported features.
 *
 * Business Rules:
 * - FetchXML elements map to SQL clauses
 * - Unsupported features generate warnings but don't block transpilation
 * - Output SQL is a best-effort representation
 */

/**
 * Result of FetchXML to SQL transpilation.
 */
export interface FetchXmlToSqlResult {
	readonly success: boolean;
	readonly sql: string;
	readonly warnings: readonly TranspilationWarning[];
	readonly error?: string;
}

/**
 * Warning for FetchXML features that cannot be fully represented in SQL.
 */
export interface TranspilationWarning {
	readonly message: string;
	readonly feature: string;
}

/**
 * Parsed attribute from FetchXML.
 */
interface ParsedAttribute {
	name: string;
	alias?: string | undefined;
	tableAlias?: string | undefined;
	/** Aggregate function: count, countcolumn, sum, avg, min, max */
	aggregate?: string | undefined;
	/** Whether this column is used for GROUP BY */
	groupby?: boolean | undefined;
	/** Whether DISTINCT is applied to this aggregate */
	distinct?: boolean | undefined;
	/** End position in XML for comment association */
	endPosition?: number | undefined;
	/** Trailing comment text */
	trailingComment?: string | undefined;
}

/**
 * Parsed link-entity from FetchXML.
 */
interface ParsedLinkEntity {
	name: string;
	from: string;
	to: string;
	alias?: string | undefined;
	linkType: string;
	attributes: ParsedAttribute[];
}

/**
 * Parsed condition from FetchXML.
 */
interface ParsedCondition {
	attribute: string;
	operator: string;
	value?: string;
	values?: string[];
	/** End position in XML for comment association */
	endPosition?: number | undefined;
	/** Trailing comment text */
	trailingComment?: string | undefined;
}

/**
 * Parsed filter from FetchXML.
 * Note: Nested filters are not supported due to regex parsing limitations.
 * All conditions are flattened into a single AND/OR group.
 */
interface ParsedFilter {
	type: 'and' | 'or';
	conditions: ParsedCondition[];
	/** End position in XML for comment association */
	endPosition?: number | undefined;
	/** Trailing comment text */
	trailingComment?: string | undefined;
}

/**
 * Parsed comment from FetchXML.
 */
interface ParsedComment {
	text: string;
	position: number;
}

/**
 * Parsed order from FetchXML.
 */
interface ParsedOrder {
	/** Attribute name for non-aggregate queries */
	attribute?: string | undefined;
	/** Alias for aggregate queries (ORDER BY alias instead of attribute) */
	alias?: string | undefined;
	descending: boolean;
	/** End position in XML for comment association */
	endPosition?: number | undefined;
	/** Trailing comment text */
	trailingComment?: string | undefined;
}

/**
 * Transpiles FetchXML to SQL strings.
 */
export class FetchXmlToSqlTranspiler {
	/**
	 * Transpiles FetchXML to SQL.
	 *
	 * @param fetchXml - The FetchXML string to transpile
	 * @returns Transpilation result with SQL and any warnings
	 */
	public transpile(fetchXml: string): FetchXmlToSqlResult {
		const warnings: TranspilationWarning[] = [];

		try {
			const trimmed = fetchXml.trim();
			if (trimmed === '') {
				return {
					success: false,
					sql: '',
					warnings: [],
					error: 'FetchXML cannot be empty',
				};
			}

			// Check for unsupported features (paging only now - we support aggregates)
			this.checkUnsupportedFeatures(trimmed, warnings);

			// Parse XML comments
			const comments = this.parseXmlComments(trimmed);

			// Parse fetch element attributes
			const top = this.extractAttribute(trimmed, 'fetch', 'top');
			const distinctStr = this.extractAttribute(trimmed, 'fetch', 'distinct');
			const distinct = distinctStr?.toLowerCase() === 'true';

			// Parse entity
			const entityName = this.extractAttribute(trimmed, 'entity', 'name');
			if (!entityName) {
				return {
					success: false,
					sql: '',
					warnings,
					error: 'Could not find entity name in FetchXML',
				};
			}

			// Parse attributes
			const hasAllAttributes = this.hasAllAttributes(trimmed);
			const attributes = hasAllAttributes
				? []
				: this.parseAttributes(trimmed);

			// Parse link-entities
			const linkEntities = this.parseLinkEntities(trimmed);

			// Parse filters
			const filter = this.parseFilter(trimmed);

			// Parse orders
			const orders = this.parseOrders(trimmed);

			// Associate comments with their nearest preceding elements
			const leadingComments = this.associateComments(
				trimmed,
				comments,
				attributes,
				filter,
				orders
			);

			// Build SQL
			const sql = this.buildSql({
				entityName,
				top,
				distinct,
				hasAllAttributes,
				attributes,
				linkEntities,
				filter,
				orders,
				leadingComments,
			});

			return {
				success: true,
				sql,
				warnings,
			};
		} catch (error) {
			return {
				success: false,
				sql: '',
				warnings,
				error: error instanceof Error ? error.message : 'Transpilation failed',
			};
		}
	}

	/**
	 * Checks for FetchXML features that cannot be fully represented in SQL.
	 */
	private checkUnsupportedFeatures(
		fetchXml: string,
		warnings: TranspilationWarning[]
	): void {
		// Check for paging - this is genuinely unsupported
		if (
			this.hasAttribute(fetchXml, 'fetch', 'page') ||
			this.hasAttribute(fetchXml, 'fetch', 'paging-cookie')
		) {
			warnings.push({
				message:
					'Paging is handled differently in SQL. Only TOP clause is supported.',
				feature: 'paging',
			});
		}

		// Check for count attribute on fetch - will be converted to TOP
		if (this.hasAttribute(fetchXml, 'fetch', 'count')) {
			warnings.push({
				message: 'Count attribute will be converted to TOP.',
				feature: 'count',
			});
		}

		// Note: aggregate, distinct, groupby, and aggregate functions are now supported
	}

	/**
	 * Checks if an element has a specific attribute.
	 */
	private hasAttribute(
		xml: string,
		elementName: string,
		attributeName: string
	): boolean {
		const pattern = new RegExp(
			`<${elementName}[^>]*\\s${attributeName}\\s*=`,
			'i'
		);
		return pattern.test(xml);
	}

	/**
	 * Extracts an attribute value from an element.
	 */
	private extractAttribute(
		xml: string,
		elementName: string,
		attributeName: string
	): string | undefined {
		const pattern = new RegExp(
			`<${elementName}[^>]*\\s${attributeName}\\s*=\\s*["']([^"']+)["']`,
			'i'
		);
		const match = xml.match(pattern);
		return match?.[1];
	}

	/**
	 * Checks if an entity or link-entity has all-attributes.
	 */
	private hasAllAttributes(xml: string): boolean {
		// Check for all-attributes directly in entity (not in link-entity)
		const entityMatch = xml.match(
			/<entity[^>]*>([\s\S]*?)(?:<link-entity|<\/entity>)/i
		);
		if (entityMatch) {
			return /<all-attributes\s*\/?>/i.test(entityMatch[1] ?? '');
		}
		return /<all-attributes\s*\/?>/i.test(xml);
	}

	/**
	 * Parses attribute elements from the main entity.
	 * Captures end positions for comment association.
	 */
	private parseAttributes(xml: string): ParsedAttribute[] {
		const attributes: ParsedAttribute[] = [];

		// Get content of main entity (before link-entities) with its start offset
		const entityMatch = xml.match(
			/<entity[^>]*>([\s\S]*?)(?:<link-entity|<\/entity>)/i
		);
		const entityContent = entityMatch?.[1] ?? '';
		// Calculate offset: find where entity content starts in original XML
		const entityContentStart = entityMatch?.index !== undefined
			? xml.indexOf(entityContent, entityMatch.index)
			: 0;

		// Parse attribute elements
		const attrPattern =
			/<attribute\s+([^>]*?)(?:\/>|>)/gi;
		let match;

		while ((match = attrPattern.exec(entityContent)) !== null) {
			const attrString = match[1] ?? '';
			const name = this.extractAttrValue(attrString, 'name');
			const alias = this.extractAttrValue(attrString, 'alias');
			const aggregate = this.extractAttrValue(attrString, 'aggregate');
			const groupbyStr = this.extractAttrValue(attrString, 'groupby');
			const distinctStr = this.extractAttrValue(attrString, 'distinct');

			if (name) {
				// Calculate end position in original XML
				const endPosition = entityContentStart + match.index + match[0].length;
				attributes.push({
					name,
					alias,
					aggregate,
					groupby: groupbyStr?.toLowerCase() === 'true',
					distinct: distinctStr?.toLowerCase() === 'true',
					endPosition,
				});
			}
		}

		return attributes;
	}

	/**
	 * Extracts attribute value from an attribute string.
	 */
	private extractAttrValue(
		attrString: string,
		attrName: string
	): string | undefined {
		// Try double-quoted value first, then single-quoted
		// This correctly handles apostrophes in double-quoted values like value="O'Brien"
		const doubleQuotePattern = new RegExp(
			`${attrName}\\s*=\\s*"([^"]*)"`,
			'i'
		);
		const doubleMatch = attrString.match(doubleQuotePattern);
		if (doubleMatch) {
			return doubleMatch[1];
		}

		const singleQuotePattern = new RegExp(
			`${attrName}\\s*=\\s*'([^']*)'`,
			'i'
		);
		const singleMatch = attrString.match(singleQuotePattern);
		return singleMatch?.[1];
	}

	/**
	 * Parses link-entity elements.
	 */
	private parseLinkEntities(xml: string): ParsedLinkEntity[] {
		const linkEntities: ParsedLinkEntity[] = [];

		// Find all link-entity elements (simplified - doesn't handle nesting)
		const linkPattern =
			/<link-entity\s+([^>]*)>([\s\S]*?)<\/link-entity>/gi;
		let match;

		while ((match = linkPattern.exec(xml)) !== null) {
			const attrString = match[1] ?? '';
			const content = match[2] ?? '';

			const name = this.extractAttrValue(attrString, 'name');
			const from = this.extractAttrValue(attrString, 'from');
			const to = this.extractAttrValue(attrString, 'to');
			const alias = this.extractAttrValue(attrString, 'alias');
			const linkType = this.extractAttrValue(attrString, 'link-type') ?? 'inner';

			if (name && from && to) {
				// Parse attributes within link-entity
				const attributes = this.parseLinkEntityAttributes(content);

				linkEntities.push({
					name,
					from,
					to,
					alias,
					linkType,
					attributes,
				});
			}
		}

		return linkEntities;
	}

	/**
	 * Parses attributes within a link-entity.
	 */
	private parseLinkEntityAttributes(content: string): ParsedAttribute[] {
		const attributes: ParsedAttribute[] = [];

		// Check for all-attributes
		if (/<all-attributes\s*\/?>/i.test(content)) {
			// Return empty to indicate SELECT * for this entity
			return [];
		}

		const attrPattern = /<attribute\s+([^>]*?)(?:\/>|>)/gi;
		let match;

		while ((match = attrPattern.exec(content)) !== null) {
			const attrString = match[1] ?? '';
			const name = this.extractAttrValue(attrString, 'name');
			const alias = this.extractAttrValue(attrString, 'alias');

			if (name) {
				attributes.push({ name, alias });
			}
		}

		return attributes;
	}

	/**
	 * Parses filter element from FetchXML.
	 * Note: Nested filters are flattened - all conditions are extracted
	 * and joined with the parent filter's type (AND/OR).
	 * Captures end position for comment association.
	 */
	private parseFilter(xml: string): ParsedFilter | null {
		// Find the first filter element in the main entity
		const filterMatch = xml.match(
			/<filter([^>]*)>([\s\S]*)<\/filter>/i
		);

		if (!filterMatch) {
			return null;
		}

		const attrString = filterMatch[1] ?? '';
		const content = filterMatch[2] ?? '';

		const filterType =
			(this.extractAttrValue(attrString, 'type')?.toLowerCase() as
				| 'and'
				| 'or') ?? 'and';

		// Calculate filter start position for condition offsets
		const filterStartIndex = filterMatch.index ?? 0;
		const contentStart = xml.indexOf(content, filterStartIndex);

		// Parse all conditions (including those in nested filters)
		const conditions = this.parseConditions(content, contentStart);

		// Calculate end position (after </filter>)
		const endPosition = filterStartIndex + filterMatch[0].length;

		return {
			type: filterType,
			conditions,
			endPosition,
		};
	}

	/**
	 * Parses condition elements.
	 * Captures end positions for comment association.
	 */
	private parseConditions(content: string, contentOffset: number = 0): ParsedCondition[] {
		const conditions: ParsedCondition[] = [];

		// Parse simple conditions (not inside nested filters)
		const conditionPattern =
			/<condition\s+([^>]*?)(?:\/>|>([\s\S]*?)<\/condition>)/gi;
		let match;

		while ((match = conditionPattern.exec(content)) !== null) {
			const attrString = match[1] ?? '';
			const innerContent = match[2];

			const attribute = this.extractAttrValue(attrString, 'attribute');
			const operator = this.extractAttrValue(attrString, 'operator');
			const value = this.extractAttrValue(attrString, 'value');

			if (attribute && operator) {
				// Calculate end position in original XML
				const endPosition = contentOffset + match.index + match[0].length;
				const condition: ParsedCondition = { attribute, operator, endPosition };

				if (value !== undefined) {
					condition.value = value;
				}

				// Parse values for IN operator
				if (innerContent) {
					const values = this.parseConditionValues(innerContent);
					if (values.length > 0) {
						condition.values = values;
					}
				}

				conditions.push(condition);
			}
		}

		return conditions;
	}

	/**
	 * Parses value elements inside a condition.
	 */
	private parseConditionValues(content: string): string[] {
		const values: string[] = [];
		const valuePattern = /<value[^>]*>([^<]*)<\/value>/gi;
		let match;

		while ((match = valuePattern.exec(content)) !== null) {
			const value = match[1];
			if (value !== undefined) {
				values.push(value);
			}
		}

		return values;
	}

	/**
	 * Parses XML comments from FetchXML.
	 * Extracts all <!-- ... --> comments with their positions.
	 */
	private parseXmlComments(xml: string): ParsedComment[] {
		const comments: ParsedComment[] = [];
		const commentPattern = /<!--([\s\S]*?)-->/g;
		let match;

		while ((match = commentPattern.exec(xml)) !== null) {
			const text = match[1]?.trim() ?? '';
			if (text.length > 0) {
				// Handle multi-line comments - split into separate lines
				const lines = text.split('\n').map((line) => line.trim()).filter((line) => line);
				for (const line of lines) {
					comments.push({
						text: line,
						position: match.index,
					});
				}
			}
		}

		return comments;
	}

	/**
	 * Parses order elements.
	 * Captures end positions for comment association.
	 * Supports both 'attribute' (regular queries) and 'alias' (aggregate queries).
	 */
	private parseOrders(xml: string): ParsedOrder[] {
		const orders: ParsedOrder[] = [];
		const orderPattern = /<order\s+([^>]*?)\/?>/gi;
		let match;

		while ((match = orderPattern.exec(xml)) !== null) {
			const attrString = match[1] ?? '';
			const attribute = this.extractAttrValue(attrString, 'attribute');
			const alias = this.extractAttrValue(attrString, 'alias');
			const descendingStr = this.extractAttrValue(attrString, 'descending');

			// Accept order if it has either attribute or alias
			if (attribute || alias) {
				// Calculate end position
				const endPosition = match.index + match[0].length;
				orders.push({
					attribute,
					alias,
					descending: descendingStr?.toLowerCase() === 'true',
					endPosition,
				});
			}
		}

		return orders;
	}

	/**
	 * Associates comments with their nearest preceding elements.
	 * Comments appearing before <fetch> are returned as leading comments.
	 * Comments appearing after elements are attached to those elements.
	 *
	 * @returns Leading comments (those before <fetch>)
	 */
	private associateComments(
		xml: string,
		comments: ParsedComment[],
		attributes: ParsedAttribute[],
		filter: ParsedFilter | null,
		orders: ParsedOrder[]
	): ParsedComment[] {
		if (comments.length === 0) {
			return [];
		}

		// Find where <fetch> starts - comments before this are leading comments
		const fetchStart = xml.indexOf('<fetch');
		const leadingComments: ParsedComment[] = [];

		// Collect all elements with positions, sorted by position
		interface PositionedElement {
			endPosition: number;
			element: ParsedAttribute | ParsedCondition | ParsedFilter | ParsedOrder;
		}
		const elements: PositionedElement[] = [];

		for (const attr of attributes) {
			if (attr.endPosition !== undefined) {
				elements.push({ endPosition: attr.endPosition, element: attr });
			}
		}

		if (filter) {
			// Add individual conditions
			for (const condition of filter.conditions) {
				if (condition.endPosition !== undefined) {
					elements.push({ endPosition: condition.endPosition, element: condition });
				}
			}
			// Also add the filter itself (for comments after </filter>)
			if (filter.endPosition !== undefined) {
				elements.push({ endPosition: filter.endPosition, element: filter });
			}
		}

		for (const order of orders) {
			if (order.endPosition !== undefined) {
				elements.push({ endPosition: order.endPosition, element: order });
			}
		}

		// Sort elements by end position
		elements.sort((a, b) => a.endPosition - b.endPosition);

		// Associate each comment with the nearest preceding element
		for (const comment of comments) {
			if (comment.position < fetchStart) {
				// Comment is before <fetch> - it's a leading comment
				leadingComments.push(comment);
				continue;
			}

			// Find the element that ends just before this comment
			let bestMatch: PositionedElement | undefined;
			for (const elem of elements) {
				if (elem.endPosition <= comment.position) {
					bestMatch = elem;
				} else {
					break; // Elements are sorted, no need to continue
				}
			}

			if (bestMatch) {
				// Attach comment to the element
				bestMatch.element.trailingComment = comment.text;
			}
		}

		return leadingComments;
	}

	/**
	 * Builds SQL from parsed FetchXML components.
	 * Generates multi-line SQL with inline comments for better readability.
	 */
	private buildSql(params: {
		entityName: string;
		top?: string | undefined;
		distinct?: boolean | undefined;
		hasAllAttributes: boolean;
		attributes: ParsedAttribute[];
		linkEntities: ParsedLinkEntity[];
		filter: ParsedFilter | null;
		orders: ParsedOrder[];
		leadingComments?: ParsedComment[];
	}): string {
		const lines: string[] = [];

		// Leading comments (before SELECT)
		if (params.leadingComments && params.leadingComments.length > 0) {
			for (const comment of params.leadingComments) {
				lines.push(`-- ${comment.text}`);
			}
		}

		// SELECT clause with optional DISTINCT and TOP
		let selectLine = params.distinct ? 'SELECT DISTINCT' : 'SELECT';
		if (params.top) {
			selectLine += ` TOP ${params.top}`;
		}
		lines.push(selectLine);

		// Columns - each on its own line with indentation
		const columnLines = this.buildMultilineColumnList(params);
		lines.push(...columnLines);

		// FROM clause
		lines.push(`FROM ${params.entityName}`);

		// JOINs - each on its own line
		for (const link of params.linkEntities) {
			const joinType = link.linkType === 'outer' ? 'LEFT JOIN' : 'JOIN';
			const aliasClause = link.alias ? ` ${link.alias}` : '';
			lines.push(
				`${joinType} ${link.name}${aliasClause} ON ${link.alias ?? link.name}.${link.from} = ${params.entityName}.${link.to}`
			);
		}

		// WHERE clause - with inline comments on conditions
		if (params.filter) {
			const whereLines = this.buildMultilineWhereClause(params.filter);
			if (whereLines.length > 0) {
				lines.push(...whereLines);
			}
		}

		// GROUP BY clause
		const groupByColumns = params.attributes.filter((attr) => attr.groupby);
		if (groupByColumns.length > 0) {
			const groupByList = groupByColumns.map((attr) => attr.name).join(', ');
			lines.push(`GROUP BY ${groupByList}`);
		}

		// ORDER BY clause - with inline comments
		if (params.orders.length > 0) {
			const orderLines = this.buildMultilineOrderByClause(params.orders);
			lines.push(...orderLines);
		}

		return lines.join('\n');
	}

	/**
	 * Builds multi-line column list with inline comments.
	 */
	private buildMultilineColumnList(params: {
		hasAllAttributes: boolean;
		attributes: ParsedAttribute[];
		linkEntities: ParsedLinkEntity[];
	}): string[] {
		const lines: string[] = [];

		if (params.hasAllAttributes && params.linkEntities.length === 0) {
			lines.push('  *');
			return lines;
		}

		// Collect all column expressions with their comments
		interface ColumnEntry {
			expression: string;
			comment?: string | undefined;
		}
		const columns: ColumnEntry[] = [];

		// Main entity columns
		if (params.hasAllAttributes) {
			columns.push({ expression: '*' });
		} else {
			for (const attr of params.attributes) {
				const columnExpr = this.buildColumnExpression(attr);
				columns.push({ expression: columnExpr, comment: attr.trailingComment });
			}
		}

		// Link entity columns
		for (const link of params.linkEntities) {
			const prefix = link.alias ?? link.name;
			if (link.attributes.length === 0) {
				columns.push({ expression: `${prefix}.*` });
			} else {
				for (const attr of link.attributes) {
					const col = `${prefix}.${attr.name}`;
					if (attr.alias) {
						columns.push({ expression: `${col} AS ${attr.alias}`, comment: attr.trailingComment });
					} else {
						columns.push({ expression: col, comment: attr.trailingComment });
					}
				}
			}
		}

		// Format columns - last one without comma
		for (let i = 0; i < columns.length; i++) {
			const col = columns[i];
			if (col === undefined) continue;

			const isLast = i === columns.length - 1;
			let line = `  ${col.expression}${isLast ? '' : ','}`;

			if (col.comment) {
				line += ` -- ${col.comment}`;
			}

			lines.push(line);
		}

		// If no columns, default to *
		if (lines.length === 0) {
			lines.push('  *');
		}

		return lines;
	}

	/**
	 * Builds multi-line WHERE clause with inline comments.
	 */
	private buildMultilineWhereClause(filter: ParsedFilter): string[] {
		const lines: string[] = [];

		if (filter.conditions.length === 0) {
			return lines;
		}

		const connector = filter.type === 'or' ? 'OR' : 'AND';

		for (let i = 0; i < filter.conditions.length; i++) {
			const condition = filter.conditions[i];
			if (condition === undefined) continue;

			const conditionSql = this.buildConditionSql(condition);
			if (!conditionSql) continue;

			let line: string;
			if (i === 0) {
				// First condition starts with WHERE
				line = `WHERE ${conditionSql}`;
			} else {
				// Subsequent conditions with AND/OR
				line = `  ${connector} ${conditionSql}`;
			}

			// Add trailing comment if present
			if (condition.trailingComment) {
				line += ` -- ${condition.trailingComment}`;
			}

			lines.push(line);
		}

		// Add filter-level trailing comment after all conditions
		if (filter.trailingComment && lines.length > 0) {
			// Append to the last condition line
			lines[lines.length - 1] += ` -- ${filter.trailingComment}`;
		}

		return lines;
	}

	/**
	 * Builds multi-line ORDER BY clause with inline comments.
	 * Uses alias for aggregate queries, attribute for regular queries.
	 */
	private buildMultilineOrderByClause(orders: ParsedOrder[]): string[] {
		const lines: string[] = [];

		for (let i = 0; i < orders.length; i++) {
			const order = orders[i];
			if (order === undefined) continue;

			const direction = order.descending ? 'DESC' : 'ASC';
			const isLast = i === orders.length - 1;
			// Use alias for aggregate queries, attribute for regular queries
			const orderColumn = order.alias ?? order.attribute;

			let line: string;
			if (i === 0) {
				// First order starts with ORDER BY
				line = `ORDER BY ${orderColumn} ${direction}${isLast ? '' : ','}`;
			} else {
				// Subsequent orders with indentation
				line = `  ${orderColumn} ${direction}${isLast ? '' : ','}`;
			}

			// Add trailing comment if present
			if (order.trailingComment) {
				line += ` -- ${order.trailingComment}`;
			}

			lines.push(line);
		}

		return lines;
	}

	/**
	 * Builds the column list for SELECT.
	 * Handles regular columns and aggregate functions.
	 */
	private buildColumnList(params: {
		hasAllAttributes: boolean;
		attributes: ParsedAttribute[];
		linkEntities: ParsedLinkEntity[];
	}): string {
		if (params.hasAllAttributes && params.linkEntities.length === 0) {
			return '*';
		}

		const columns: string[] = [];

		// Main entity columns
		if (params.hasAllAttributes) {
			columns.push('*');
		} else {
			for (const attr of params.attributes) {
				const columnExpr = this.buildColumnExpression(attr);
				columns.push(columnExpr);
			}
		}

		// Link entity columns
		for (const link of params.linkEntities) {
			const prefix = link.alias ?? link.name;
			if (link.attributes.length === 0) {
				columns.push(`${prefix}.*`);
			} else {
				for (const attr of link.attributes) {
					const col = `${prefix}.${attr.name}`;
					if (attr.alias) {
						columns.push(`${col} AS ${attr.alias}`);
					} else {
						columns.push(col);
					}
				}
			}
		}

		return columns.length > 0 ? columns.join(', ') : '*';
	}

	/**
	 * Builds a single column expression, wrapping in aggregate function if needed.
	 */
	private buildColumnExpression(attr: ParsedAttribute): string {
		let expr: string;

		if (attr.aggregate) {
			// Build aggregate function expression
			expr = this.buildAggregateExpression(attr);
		} else {
			// Regular column reference
			expr = attr.name;
		}

		// Add alias if present
		if (attr.alias) {
			return `${expr} AS ${attr.alias}`;
		}

		return expr;
	}

	/**
	 * Builds an aggregate function expression.
	 * Maps FetchXML aggregate types to SQL aggregate functions.
	 */
	private buildAggregateExpression(attr: ParsedAttribute): string {
		const aggType = attr.aggregate?.toLowerCase();
		const distinctKeyword = attr.distinct ? 'DISTINCT ' : '';

		switch (aggType) {
			case 'count':
				// In FetchXML, aggregate="count" is used for COUNT(*)
				// The column name is just a placeholder (usually the primary key)
				return 'COUNT(*)';

			case 'countcolumn':
				// COUNT(column) - counts non-null values
				return `COUNT(${distinctKeyword}${attr.name})`;

			case 'sum':
				return `SUM(${distinctKeyword}${attr.name})`;

			case 'avg':
				return `AVG(${distinctKeyword}${attr.name})`;

			case 'min':
				return `MIN(${attr.name})`;

			case 'max':
				return `MAX(${attr.name})`;

			default:
				// Unknown aggregate, just use the column name
				return attr.name;
		}
	}

	/**
	 * Builds WHERE clause from filter.
	 */
	private buildWhereClause(filter: ParsedFilter): string {
		const parts: string[] = [];

		// Build conditions
		for (const condition of filter.conditions) {
			const conditionSql = this.buildConditionSql(condition);
			if (conditionSql) {
				parts.push(conditionSql);
			}
		}

		const connector = filter.type === 'or' ? ' OR ' : ' AND ';
		return parts.join(connector);
	}

	/**
	 * Builds SQL for a single condition.
	 */
	private buildConditionSql(condition: ParsedCondition): string {
		const attr = condition.attribute;
		const op = condition.operator.toLowerCase();
		const value = condition.value;

		switch (op) {
			case 'eq':
				return `${attr} = ${this.formatValue(value)}`;
			case 'ne':
				return `${attr} <> ${this.formatValue(value)}`;
			case 'lt':
				return `${attr} < ${this.formatValue(value)}`;
			case 'le':
				return `${attr} <= ${this.formatValue(value)}`;
			case 'gt':
				return `${attr} > ${this.formatValue(value)}`;
			case 'ge':
				return `${attr} >= ${this.formatValue(value)}`;
			case 'like':
				return `${attr} LIKE ${this.formatValue(value)}`;
			case 'not-like':
				return `${attr} NOT LIKE ${this.formatValue(value)}`;
			case 'begins-with':
				return `${attr} LIKE ${this.formatValue((value ?? '') + '%')}`;
			case 'not-begin-with':
				return `${attr} NOT LIKE ${this.formatValue((value ?? '') + '%')}`;
			case 'ends-with':
				return `${attr} LIKE ${this.formatValue('%' + (value ?? ''))}`;
			case 'not-end-with':
				return `${attr} NOT LIKE ${this.formatValue('%' + (value ?? ''))}`;
			case 'null':
				return `${attr} IS NULL`;
			case 'not-null':
				return `${attr} IS NOT NULL`;
			case 'in':
				if (condition.values && condition.values.length > 0) {
					const vals = condition.values.map((v) => this.formatValue(v)).join(', ');
					return `${attr} IN (${vals})`;
				}
				return `${attr} IN (${this.formatValue(value)})`;
			case 'not-in':
				if (condition.values && condition.values.length > 0) {
					const vals = condition.values.map((v) => this.formatValue(v)).join(', ');
					return `${attr} NOT IN (${vals})`;
				}
				return `${attr} NOT IN (${this.formatValue(value)})`;
			default:
				// For unknown operators, try a basic format
				return `${attr} ${op.toUpperCase()} ${this.formatValue(value)}`;
		}
	}

	/**
	 * Formats a value for SQL output.
	 */
	private formatValue(value: string | undefined): string {
		if (value === undefined) {
			return "''";
		}

		// Check if it's a number
		if (/^-?\d+(\.\d+)?$/.test(value)) {
			return value;
		}

		// Escape single quotes and wrap in quotes
		return `'${value.replace(/'/g, "''")}'`;
	}
}
