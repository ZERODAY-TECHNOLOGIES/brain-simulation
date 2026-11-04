using BrainSim.Engine.Dreams;

namespace BrainSim.Engine.Tests;

public class DreamClausesTests
{
    [Fact]
    public void Clauses_drop_markdown_questions_and_dialogue_shape_in_any_language()
    {
        const string fragment = "Am avut o zi grea, șeful a țipat la mine.\n**Îmi pare rău.** Vrei să vorbim despre asta?\n- caracatițele au trei inimi\n- sângele lor e albastru";

        var clauses = Dreamer.Clauses(fragment).ToList();

        Assert.Equal(["am avut o zi grea", "șeful a țipat la mine", "îmi pare rău", "caracatițele au trei inimi", "sângele lor e albastru"], clauses);
    }
}
