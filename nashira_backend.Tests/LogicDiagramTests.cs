using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// A snippet whose behaviour lives in CODE has to explain itself. A type and a name say what a
// `ping` does; they say nothing about a `python_snippet`.
//
// The rule is FlowWeaver's, ported verbatim — same types, same directives, same message —
// because a parity rule that reworded its own refusal would leave an operator comparing two
// products by their error text and concluding they differ. These tests pin the parts that
// would drift first.
public class LogicDiagramTests
{
    private const string Valid = "graph TD\n  in[input] --> out[output]";

    [Theory]
    [InlineData("python_snippet")]
    [InlineData("python")]
    [InlineData("transform")]
    [InlineData("jmespath")]
    public void A_code_carrying_type_must_carry_a_diagram(string type)
    {
        var error = SnippetCatalog.ValidateLogicDiagram(type, null);

        Assert.NotNull(error);
        // The message has to be actionable: the field, the type, and where the conventions are.
        Assert.Contains("logic_diagram_mermaid is required", error);
        Assert.Contains(type, error);
        Assert.Contains("Skills/mermaid.md", error);
    }

    // The other half, and the one that keeps the rule narrow. A handler-defined type's
    // behaviour is readable from its type, so demanding a diagram would be ceremony.
    [Theory]
    [InlineData("ssh")]
    [InlineData("ping")]
    [InlineData("rest_call")]
    [InlineData("email_send")]
    public void A_handler_defined_type_does_not_need_one(string type)
    {
        Assert.Null(SnippetCatalog.ValidateLogicDiagram(type, null));
    }

    [Fact]
    public void A_valid_diagram_is_accepted()
    {
        Assert.Null(SnippetCatalog.ValidateLogicDiagram("python_snippet", Valid));
    }

    // Applies to EVERY type, not just the required ones: a field that accepts anything
    // eventually holds a pasted stack trace, and the first person to find out is whoever
    // opens the renderer.
    [Fact]
    public void A_value_that_is_not_a_diagram_is_refused_even_on_an_optional_type()
    {
        var error = SnippetCatalog.ValidateLogicDiagram("ssh", "import os\nos.system('ls')");

        Assert.NotNull(error);
        Assert.Contains("must start with a Mermaid directive", error);
        // Quotes what it actually found, so the author can see their paste.
        Assert.Contains("import os", error);
    }

    [Fact]
    public void Comments_and_blank_lines_before_the_directive_are_skipped()
    {
        var diagram = "\n%% what this step does\n\nflowchart LR\n  a --> b";

        Assert.Null(SnippetCatalog.ValidateLogicDiagram("transform", diagram));
    }

    [Fact]
    public void A_diagram_of_only_comments_has_no_content()
    {
        var error = SnippetCatalog.ValidateLogicDiagram("transform", "%% just a note\n%% and another");

        Assert.Equal("logic_diagram_mermaid has no non-blank content", error);
    }

    // Whitespace-only is the same as absent, which matters because a form posts "" rather
    // than omitting the field.
    [Fact]
    public void Whitespace_only_reads_as_absent()
    {
        Assert.NotNull(SnippetCatalog.ValidateLogicDiagram("transform", "   \n  "));
        Assert.Null(SnippetCatalog.ValidateLogicDiagram("ssh", "   \n  "));
    }

    [Theory]
    [InlineData("sequenceDiagram\n  a->>b: hi")]
    [InlineData("stateDiagram-v2\n  [*] --> Idle")]
    [InlineData("MINDMAP\n  root")]          // the directive check is case-insensitive
    public void Every_accepted_directive_family_is_recognised(string diagram)
    {
        Assert.Null(SnippetCatalog.ValidateLogicDiagram("python_snippet", diagram));
    }

    // The parity assertion itself. If someone adds a type to one product's list and not the
    // other, the two stop refusing the same payload — which is the whole fault this change
    // was written to remove.
    [Fact]
    public void The_required_set_is_the_one_FlowWeaver_uses()
    {
        string[] flowWeaverRequires = ["python_snippet", "python", "transform", "jmespath"];

        foreach (var type in flowWeaverRequires)
            Assert.NotNull(SnippetCatalog.ValidateLogicDiagram(type, null));

        // And nothing beyond it: a wider set here would refuse payloads FlowWeaver accepts.
        foreach (var type in new[] { "ssh", "ping", "rest_call", "git", "report",
                                     "slack_message", "ansible_playbook", "mcp_call" })
            Assert.Null(SnippetCatalog.ValidateLogicDiagram(type, null));
    }
}
