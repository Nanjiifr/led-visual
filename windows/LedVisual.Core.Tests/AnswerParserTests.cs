using LedVisual.Core.Services;
using Xunit;

namespace LedVisual.Core.Tests;

public sealed class AnswerParserTests
{
    [Fact]
    public void SingleNumberAnswers()
    {
        Assert.Equal(new[] { 1 }, AnswerParser.Parse("1"));
        Assert.Equal(new[] { 3 }, AnswerParser.Parse("  3\n"));
        Assert.Equal(new[] { 4 }, AnswerParser.Parse("`4`"));
        Assert.Equal(new[] { 12 }, AnswerParser.Parse("12"));
    }

    [Fact]
    public void MultiSelectAnswers()
    {
        Assert.Equal(new[] { 1, 3 }, AnswerParser.Parse("1,3"));
        Assert.Equal(new[] { 1, 3, 4 }, AnswerParser.Parse("1, 3, 4"));
        Assert.Equal(new[] { 2, 4 }, AnswerParser.Parse("4, 2, 2"));
        Assert.Equal(new[] { 1, 3 }, AnswerParser.Parse("1 and 3"));
        Assert.Equal(new[] { 2, 5 }, AnswerParser.Parse("2 5"));
    }

    [Fact]
    public void LetterAndLabeledFallbacks()
    {
        Assert.Equal(new[] { 3 }, AnswerParser.Parse("C"));
        Assert.Equal(new[] { 1, 3 }, AnswerParser.Parse("A, C"));
        Assert.Equal(new[] { 3 }, AnswerParser.Parse("The correct answer is 3."));
        Assert.Equal(new[] { 1, 4 }, AnswerParser.Parse("Correct options: 1, 4"));
        Assert.Equal(new[] { 2, 3 }, AnswerParser.Parse("Reasoning...\n2, 3"));
    }

    [Fact]
    public void FailureAndInvalidOutputs()
    {
        Assert.Null(AnswerParser.Parse(""));
        Assert.Null(AnswerParser.Parse(null));
        Assert.Null(AnswerParser.Parse("NONE"));
        Assert.Null(AnswerParser.Parse("none"));
        Assert.Null(AnswerParser.Parse("ERROR"));
        Assert.Null(AnswerParser.Parse("0"));
        Assert.Null(AnswerParser.Parse("2026"));
        Assert.Null(AnswerParser.Parse("No multiple choice question visible."));
    }
}
