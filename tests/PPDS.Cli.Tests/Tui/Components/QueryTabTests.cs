using PPDS.Cli.Tui.Components;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Components;

[Trait("Category", "TuiUnit")]
public sealed class QueryTabTests : IDisposable
{
    public QueryTabTests()
    {
        QueryTab.ResetTabCounter();
    }

    public void Dispose()
    {
        QueryTab.ResetTabCounter();
    }

    [Fact]
    public void Constructor_DefaultState_IsReady()
    {
        var tab = new QueryTab();

        Assert.Equal("", tab.QueryText);
        Assert.False(tab.IsExecuting);
        Assert.Equal("Ready", tab.StatusText);
        Assert.Null(tab.Results);
        Assert.Null(tab.ExecutionPlan);
        Assert.False(tab.HasUnsavedChanges);
    }

    [Fact]
    public void Constructor_WithInitialQuery_SetsQueryText()
    {
        var tab = new QueryTab("SELECT name FROM account");

        Assert.Equal("SELECT name FROM account", tab.QueryText);
        Assert.False(tab.HasUnsavedChanges);
    }

    [Fact]
    public void Title_FromSelectQuery_ExtractsEntityName()
    {
        var tab = new QueryTab("SELECT name FROM account WHERE statecode = 0");

        Assert.Equal("account", tab.Title);
    }

    [Fact]
    public void Title_FromSelectQuery_CaseInsensitive()
    {
        var tab = new QueryTab("select name from Contact where statecode = 0");

        Assert.Equal("Contact", tab.Title);
    }

    [Fact]
    public void Title_FromSelectTopQuery_ExtractsEntityName()
    {
        var tab = new QueryTab("SELECT TOP 100 accountid, name FROM account");

        Assert.Equal("account", tab.Title);
    }

    [Fact]
    public void Title_EmptyQuery_FallsBackToQueryN()
    {
        var tab = new QueryTab("");

        Assert.StartsWith("Query ", tab.Title);
    }

    [Fact]
    public void Title_NoFromClause_FallsBackToQueryN()
    {
        var tab = new QueryTab("SELECT 1");

        Assert.StartsWith("Query ", tab.Title);
    }

    [Fact]
    public void HasUnsavedChanges_AfterModification_ReturnsTrue()
    {
        var tab = new QueryTab("SELECT name FROM account");

        tab.QueryText = "SELECT name, accountid FROM account";

        Assert.True(tab.HasUnsavedChanges);
    }

    [Fact]
    public void HasUnsavedChanges_AfterMarkAsSaved_ReturnsFalse()
    {
        var tab = new QueryTab("SELECT name FROM account");
        tab.QueryText = "SELECT name, accountid FROM account";

        tab.MarkAsSaved();

        Assert.False(tab.HasUnsavedChanges);
    }

    [Fact]
    public void HasUnsavedChanges_RevertToOriginal_ReturnsFalse()
    {
        var original = "SELECT name FROM account";
        var tab = new QueryTab(original);
        tab.QueryText = "SELECT name, accountid FROM account";

        tab.QueryText = original;

        Assert.False(tab.HasUnsavedChanges);
    }

    [Fact]
    public void GetDisplayTitle_NoChanges_ReturnsTitle()
    {
        var tab = new QueryTab("SELECT name FROM account");

        Assert.Equal("account", tab.GetDisplayTitle());
    }

    [Fact]
    public void GetDisplayTitle_WithChanges_AppendsStar()
    {
        var tab = new QueryTab("SELECT name FROM account");
        tab.QueryText = "SELECT name, accountid FROM account";

        Assert.Equal("account*", tab.GetDisplayTitle());
    }

    [Fact]
    public void Title_Updates_WhenQueryTextChanges()
    {
        var tab = new QueryTab("SELECT name FROM account");
        Assert.Equal("account", tab.Title);

        tab.QueryText = "SELECT name FROM contact";
        Assert.Equal("contact", tab.Title);
    }

    [Fact]
    public void TabId_IsUnique_AcrossMultipleTabs()
    {
        var tab1 = new QueryTab();
        var tab2 = new QueryTab();
        var tab3 = new QueryTab();

        Assert.NotEqual(tab1.TabId, tab2.TabId);
        Assert.NotEqual(tab2.TabId, tab3.TabId);
    }

    [Fact]
    public void GenerateTitle_StaticMethod_ExtractsEntity()
    {
        Assert.Equal("account", QueryTab.GenerateTitle("SELECT name FROM account"));
        Assert.Equal("contact", QueryTab.GenerateTitle("SELECT * FROM contact"));
    }

    [Fact]
    public void GenerateTitle_JoinQuery_ExtractsPrimaryEntity()
    {
        var title = QueryTab.GenerateTitle(
            "SELECT a.name, c.fullname FROM account a JOIN contact c ON a.accountid = c.parentcustomerid");

        Assert.Equal("account", title);
    }

    [Fact]
    public void IsExecuting_DefaultsFalse()
    {
        var tab = new QueryTab();
        Assert.False(tab.IsExecuting);
    }

    [Fact]
    public void StatusText_DefaultsToReady()
    {
        var tab = new QueryTab();
        Assert.Equal("Ready", tab.StatusText);
    }

    [Fact]
    public void StatusText_CanBeUpdated()
    {
        var tab = new QueryTab();
        tab.StatusText = "Executing...";

        Assert.Equal("Executing...", tab.StatusText);
    }

    [Fact]
    public void Pagination_Properties_DefaultCorrectly()
    {
        var tab = new QueryTab();

        Assert.Null(tab.LastExecutedSql);
        Assert.Null(tab.LastPagingCookie);
        Assert.Equal(1, tab.LastPageNumber);
    }
}
