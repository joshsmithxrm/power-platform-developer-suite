using FluentAssertions;
using PPDS.Dataverse.Services.Utilities;
using Xunit;

namespace PPDS.Dataverse.Tests.Services.Utilities;

public class FlowClientDataParserTests
{
    [Fact]
    public void ExtractConnectionReferenceLogicalNames_NullClientData_ReturnsEmptyList()
    {
        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(null);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_EmptyClientData_ReturnsEmptyList()
    {
        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames("");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_WhitespaceClientData_ReturnsEmptyList()
    {
        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames("   ");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_InvalidJson_ReturnsEmptyList()
    {
        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames("not valid json {{{");

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_PropertiesConnectionReferences_ExtractsKeys()
    {
        // Arrange
        var clientData = """
            {
                "properties": {
                    "connectionReferences": {
                        "cr_dataverse_connection": { "connectionId": "abc123" },
                        "cr_sharepoint_connection": { "connectionId": "def456" }
                    }
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain("cr_dataverse_connection");
        result.Should().Contain("cr_sharepoint_connection");
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_TopLevelConnectionReferences_ExtractsKeys()
    {
        // Arrange
        var clientData = """
            {
                "connectionReferences": {
                    "new_cr_outlook": { "api": "/apis/outlook" },
                    "new_cr_teams": { "api": "/apis/teams" }
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain("new_cr_outlook");
        result.Should().Contain("new_cr_teams");
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_DefinitionConnectionReferences_ExtractsKeys()
    {
        // Arrange
        var clientData = """
            {
                "definition": {
                    "connectionReferences": {
                        "ppds_cds_connection": {}
                    }
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().ContainSingle();
        result.Should().Contain("ppds_cds_connection");
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_NoConnectionReferences_ReturnsEmptyList()
    {
        // Arrange
        var clientData = """
            {
                "properties": {
                    "displayName": "My Flow",
                    "state": "Started"
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_EmptyConnectionReferences_ReturnsEmptyList()
    {
        // Arrange
        var clientData = """
            {
                "properties": {
                    "connectionReferences": {}
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_ConnectionReferencesNotObject_ReturnsEmptyList()
    {
        // Arrange - connectionReferences is an array instead of object
        var clientData = """
            {
                "properties": {
                    "connectionReferences": ["cr1", "cr2"]
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_PreservesCase()
    {
        // Arrange - mixed case logical names should be preserved
        var clientData = """
            {
                "properties": {
                    "connectionReferences": {
                        "CR_Dataverse_Connection": {},
                        "cr_sharepoint_connection": {},
                        "NEW_CR_OUTLOOK": {}
                    }
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain("CR_Dataverse_Connection");
        result.Should().Contain("cr_sharepoint_connection");
        result.Should().Contain("NEW_CR_OUTLOOK");
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_PropertiesTakesPrecedence()
    {
        // Arrange - when both properties.connectionReferences and top-level exist
        var clientData = """
            {
                "properties": {
                    "connectionReferences": {
                        "from_properties": {}
                    }
                },
                "connectionReferences": {
                    "from_top_level": {}
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().ContainSingle();
        result.Should().Contain("from_properties");
        result.Should().NotContain("from_top_level");
    }

    [Fact]
    public void ExtractConnectionReferenceLogicalNames_RealWorldClientData_ExtractsCorrectly()
    {
        // Arrange - realistic client data structure
        var clientData = """
            {
                "properties": {
                    "displayName": "Send Email on Record Create",
                    "definition": {
                        "triggers": {},
                        "actions": {}
                    },
                    "connectionReferences": {
                        "shared_commondataserviceforapps": {
                            "connectionReferenceLogicalName": "ppds_SharedCommonDataServiceForApps",
                            "api": {
                                "name": "shared_commondataserviceforapps"
                            }
                        },
                        "shared_office365": {
                            "connectionReferenceLogicalName": "ppds_SharedOffice365",
                            "api": {
                                "name": "shared_office365"
                            }
                        }
                    },
                    "state": "Started"
                }
            }
            """;

        // Act
        var result = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain("shared_commondataserviceforapps");
        result.Should().Contain("shared_office365");
    }
}
