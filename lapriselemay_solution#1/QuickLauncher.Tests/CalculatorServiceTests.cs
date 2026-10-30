using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Calculatrice : précédence du signe unaire face à la puissance, parenthèses obligatoires,
/// fonctions reconnues alignées sur le parseur.
/// </summary>
public class CalculatorServiceTests
{
    private static string? Eval(string expression)
        => new CalculatorService().TryCalculate(expression, out var result) ? result : null;

    [Theory]
    [InlineData("-2^2", "-4")]
    [InlineData("(-2)^2", "4")]
    [InlineData("2^-2", "0.25")]
    [InlineData("-2^-2", "-0.25")]
    [InlineData("2^3^2", "512")]
    [InlineData("2*-3", "-6")]
    [InlineData("10-2^2", "6")]
    [InlineData("1,5+1", "2.5")]
    public void Precedence_et_signes(string expression, string expected)
        => Assert.Equal(expected, Eval(expression));

    [Theory]
    [InlineData("sqrt(9")]
    [InlineData("(2+3")]
    [InlineData("2+3)")]
    [InlineData("((1+2)")]
    public void Parentheses_non_equilibrees_refusees(string expression)
        => Assert.Null(Eval(expression));

    [Fact]
    public void Exp_est_reconnue()
        => Assert.Equal("2.718281828", Eval("exp(1)"));

    [Fact]
    public void Chaque_fonction_annoncee_est_evaluable()
    {
        foreach (var function in CalculatorService.KnownFunctions)
            Assert.NotNull(Eval($"{function}(1)"));
    }
}
