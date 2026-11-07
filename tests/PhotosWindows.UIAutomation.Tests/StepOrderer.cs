using Xunit.Abstractions;
using Xunit.Sdk;

namespace PhotosWindows.UIAutomation.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class StepAttribute(int number) : Attribute
{
    public int Number { get; } = number;
}

/// <summary>Runs the end-to-end scenarios in the order of their <see cref="StepAttribute"/>.</summary>
public sealed class StepOrderer : ITestCaseOrderer
{
    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases) where TTestCase : ITestCase =>
        testCases.OrderBy(t => t.TestMethod.Method.GetCustomAttributes(typeof(StepAttribute).AssemblyQualifiedName).FirstOrDefault()?.GetConstructorArguments().FirstOrDefault() is int n ? n : int.MaxValue);
}
