using nashira_backend.Services.Ai.Skills;

namespace nashira_backend.Tests;

// The on-demand topic skills: which built-in skills they are, when a message or a
// tool call puts one in play, and how the model refers to one.
public class TopicSkillCatalogTests
{
    // The skills that stay in every turn. Everything else that ships has to be a
    // topic, so a new Skills/*.md is a conscious choice instead of a silent few
    // thousand tokens on every round-trip.
    private static readonly string[] EveryTurn = ["base.md", "nashira.md"];

    private static string SkillsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nashira.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "nashira_backend", "Skills");
    }

    private static List<TopicSkill> Topics(params string[] names)
        => names.Select(n => TopicSkillCatalog.From(n, "text of " + n)!).ToList();

    [Fact]
    public void Every_shipped_skill_is_either_a_topic_or_deliberately_every_turn()
    {
        // Top level only: Skills/vendors holds seed data, not prompt text.
        var shipped = Directory.GetFiles(SkillsDir(), "*.md").Select(Path.GetFileName).ToList();

        Assert.DoesNotContain(shipped, n => !TopicSkillCatalog.IsTopic(n!) && !EveryTurn.Contains(n));
        Assert.All(EveryTurn, n => Assert.False(TopicSkillCatalog.IsTopic(n)));
    }

    [Theory]
    [InlineData("crea un workflow que haga backup", "workflows.md")]
    [InlineData("haz commit en el repo", "git.md")]
    [InlineData("lee mi bandeja de correo", "email.md")]
    [InlineData("exporta la tabla a excel", "exports.md")]
    [InlineData("guarda el token en un secreto", "secrets.md")]
    [InlineData("¿qué permisos tiene este rol?", "governance.md")]
    [InlineData("haz ping al router", "inventory.md")]
    public void Messages_naming_a_topic_put_it_in_play(string message, string topic)
    {
        var topics = Topics("workflows.md", "git.md", "email.md", "exports.md", "secrets.md", "governance.md", "inventory.md");

        Assert.Contains(TopicSkillCatalog.LoadKeyFor(topic), TopicSkillCatalog.MatchMentions(message, topics));
    }

    // Short keywords must not fire inside longer unrelated words.
    [Theory]
    [InlineData("send me the weekly report", "git.md")]            // "repo" in "report"
    [InlineData("quiero el control del presupuesto", "governance.md")] // "rol" in "control"
    [InlineData("who am I logged in as?", "workflows.md")]
    public void Unrelated_messages_leave_the_topic_out(string message, string topic)
    {
        var topics = Topics("git.md", "governance.md", "workflows.md");

        Assert.DoesNotContain(TopicSkillCatalog.LoadKeyFor(topic), TopicSkillCatalog.MatchMentions(message, topics));
    }

    [Fact]
    public void A_topics_tools_put_it_in_play()
    {
        var topics = Topics("git.md", "email.md", "workflows.md");

        Assert.Equal(["git.md"], TopicSkillCatalog.ForTool("github_create_pr", topics).Select(t => t.Name));
        Assert.Equal(["email.md"], TopicSkillCatalog.ForTool("send_email", topics).Select(t => t.Name));
        Assert.Empty(TopicSkillCatalog.ForTool("whoami", topics));
    }

    [Theory]
    [InlineData("workflows.md")]
    [InlineData("workflows")]
    [InlineData("Skills/workflows.md")]
    public void Find_accepts_every_way_a_skill_is_referred_to(string name)
        => Assert.Equal("workflows.md", TopicSkillCatalog.Find(name, Topics("workflows.md"))?.Name);

    [Fact]
    public void Render_indexes_every_topic_and_carries_only_the_loaded_text()
    {
        var topics = Topics("git.md", "email.md");
        var loaded = new HashSet<Guid> { TopicSkillCatalog.LoadKeyFor("git.md") };

        var text = TopicSkillCatalog.Render(topics, loaded);

        Assert.Contains("`git.md`", text);
        Assert.Contains("`email.md`", text);
        Assert.Contains("text of git.md", text);
        Assert.DoesNotContain("text of email.md", text);
    }
}
