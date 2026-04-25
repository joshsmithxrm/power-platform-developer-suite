using PPDS.Cli.Tui.Testing.States;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class SolutionsScreenStateTests
{
    [Fact]
    public void InitialLoadingState()
    {
        var state = new SolutionsScreenState(
            SolutionCount: 0,
            SelectedSolutionName: null,
            SelectedSolutionVersion: null,
            SelectedIsManaged: null,
            ComponentCount: null,
            IsLoading: true,
            ShowManaged: true,
            FilterText: "",
            ErrorMessage: null);

        Assert.Equal(0, state.SolutionCount);
        Assert.True(state.IsLoading);
        Assert.True(state.ShowManaged);
        Assert.Equal("", state.FilterText);
        Assert.Null(state.ErrorMessage);
        Assert.Null(state.SelectedSolutionName);
    }

    [Fact]
    public void LoadedWithSolutions()
    {
        var state = new SolutionsScreenState(
            SolutionCount: 12,
            SelectedSolutionName: "Default Solution",
            SelectedSolutionVersion: "1.0.0.0",
            SelectedIsManaged: false,
            ComponentCount: null,
            IsLoading: false,
            ShowManaged: true,
            FilterText: "",
            ErrorMessage: null);

        Assert.Equal(12, state.SolutionCount);
        Assert.Equal("Default Solution", state.SelectedSolutionName);
        Assert.Equal("1.0.0.0", state.SelectedSolutionVersion);
        Assert.False(state.SelectedIsManaged);
        Assert.False(state.IsLoading);
        Assert.Null(state.ComponentCount);
    }

    [Fact]
    public void FilteredState()
    {
        var state = new SolutionsScreenState(
            SolutionCount: 3,
            SelectedSolutionName: "Core",
            SelectedSolutionVersion: "2.1.0.0",
            SelectedIsManaged: true,
            ComponentCount: null,
            IsLoading: false,
            ShowManaged: true,
            FilterText: "Core",
            ErrorMessage: null);

        Assert.Equal(3, state.SolutionCount);
        Assert.Equal("Core", state.FilterText);
    }

    [Fact]
    public void UnmanagedOnlyFilter()
    {
        var state = new SolutionsScreenState(
            SolutionCount: 5,
            SelectedSolutionName: null,
            SelectedSolutionVersion: null,
            SelectedIsManaged: null,
            ComponentCount: null,
            IsLoading: false,
            ShowManaged: false,
            FilterText: "",
            ErrorMessage: null);

        Assert.False(state.ShowManaged);
        Assert.Equal(5, state.SolutionCount);
    }

    [Fact]
    public void WithComponentCountLoaded()
    {
        var state = new SolutionsScreenState(
            SolutionCount: 8,
            SelectedSolutionName: "MyCustomization",
            SelectedSolutionVersion: "1.0.0.0",
            SelectedIsManaged: false,
            ComponentCount: 47,
            IsLoading: false,
            ShowManaged: true,
            FilterText: "",
            ErrorMessage: null);

        Assert.Equal(47, state.ComponentCount);
        Assert.Equal("MyCustomization", state.SelectedSolutionName);
    }

    [Fact]
    public void ErrorState()
    {
        var state = new SolutionsScreenState(
            SolutionCount: 0,
            SelectedSolutionName: null,
            SelectedSolutionVersion: null,
            SelectedIsManaged: null,
            ComponentCount: null,
            IsLoading: false,
            ShowManaged: true,
            FilterText: "",
            ErrorMessage: "Connection lost");

        Assert.Equal("Connection lost", state.ErrorMessage);
        Assert.False(state.IsLoading);
        Assert.Equal(0, state.SolutionCount);
    }

    [Fact]
    public void RecordEquality()
    {
        var a = new SolutionsScreenState(5, "Sol", "1.0", false, null, false, true, "", null);
        var b = new SolutionsScreenState(5, "Sol", "1.0", false, null, false, true, "", null);

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordInequality_DifferentFilter()
    {
        var a = new SolutionsScreenState(5, null, null, null, null, false, true, "", null);
        var b = new SolutionsScreenState(5, null, null, null, null, false, true, "Core", null);

        Assert.NotEqual(a, b);
    }
}
