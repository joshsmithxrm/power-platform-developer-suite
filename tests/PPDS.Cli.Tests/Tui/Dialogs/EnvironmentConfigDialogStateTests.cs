using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Testing.States;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

[Trait("Category", "TuiUnit")]
public class EnvironmentConfigDialogStateTests
{
    [Fact]
    public void State_Type_IsEnvironmentTypeNullable()
    {
        var state = new EnvironmentConfigDialogState(
            Title: "Test",
            Url: "https://org.crm.dynamics.com/",
            Label: "",
            Type: EnvironmentType.Sandbox,
            SelectedColorIndex: 0,
            SelectedColor: null,
            ConfigChanged: false,
            IsVisible: true);

        Assert.Equal(EnvironmentType.Sandbox, state.Type);
    }

    [Fact]
    public void State_Type_NullMeansAutoDetect()
    {
        var state = new EnvironmentConfigDialogState(
            Title: "Test",
            Url: "https://org.crm.dynamics.com/",
            Label: "",
            Type: null,
            SelectedColorIndex: 0,
            SelectedColor: null,
            ConfigChanged: false,
            IsVisible: true);

        Assert.Null(state.Type);
    }
}
