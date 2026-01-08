# Triage helper - fetch project items and parse field values
param(
    [switch]$ShowAll
)

$query = @'
query($endCursor: String) {
  node(id: "PVT_kwHOAGk32c4BLj-0") {
    ... on ProjectV2 {
      items(first: 100, after: $endCursor) {
        nodes {
          id
          content {
            ... on Issue {
              number
            }
          }
          fieldValues(first: 20) {
            nodes {
              ... on ProjectV2ItemFieldSingleSelectValue {
                field {
                  ... on ProjectV2SingleSelectField {
                    name
                  }
                }
                name
              }
              ... on ProjectV2ItemFieldTextValue {
                field {
                  ... on ProjectV2Field {
                    name
                  }
                }
                text
              }
            }
          }
        }
        pageInfo {
          hasNextPage
          endCursor
        }
      }
    }
  }
}
'@

# Fetch all pages
$allItems = @()
$endCursor = $null
do {
    if ($endCursor) {
        $result = gh api graphql -f query="$query" -f endCursor="$endCursor" 2>&1 | ConvertFrom-Json
    } else {
        $result = gh api graphql -f query="$query" 2>&1 | ConvertFrom-Json
    }

    $items = $result.data.node.items.nodes | Where-Object { $_.content.number }
    $allItems += $items

    $hasNextPage = $result.data.node.items.pageInfo.hasNextPage
    $endCursor = $result.data.node.items.pageInfo.endCursor
} while ($hasNextPage)

# Parse and output
foreach ($item in $allItems) {
    $num = $item.content.number
    $id = $item.id
    $fields = @{}
    foreach ($fv in $item.fieldValues.nodes) {
        if ($fv.field.name -and $fv.field.name -ne "Title") {
            $val = if ($fv.name) { $fv.name } else { $fv.text }
            $fields[$fv.field.name] = $val
        }
    }
    $status = $fields["Status"]
    $type = $fields["Type"]
    $priority = $fields["Priority"]
    $size = $fields["Size"]
    $target = $fields["Target"]

    if ($ShowAll -or -not ($status -and $type -and $priority -and $size)) {
        Write-Output "$num|$id|$status|$type|$priority|$size|$target"
    }
}
