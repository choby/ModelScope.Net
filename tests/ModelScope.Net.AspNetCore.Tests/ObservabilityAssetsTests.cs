using System.Text.Json;
using ModelScope.Net.AspNetCore;

namespace ModelScope.Net.AspNetCore.Tests;

public sealed class ObservabilityAssetsTests
{
    [Fact]
    public void MetricContractMatchesRuntimeInstrumentationAndUsesBoundedLabels()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Asset("metric-contract.json")));
        var root = contract.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ModelScopeTelemetry.MeterName, root.GetProperty("sourceMeter").GetString());

        var metrics = root.GetProperty("metrics");
        Assert.Equal(ModelScopeTelemetry.InvocationMetricName, metrics.GetProperty("invocations").GetProperty("otelName").GetString());
        Assert.Equal(ModelScopeTelemetry.FailureMetricName, metrics.GetProperty("failures").GetProperty("otelName").GetString());
        Assert.Equal(ModelScopeTelemetry.DurationMetricName, metrics.GetProperty("duration").GetProperty("otelName").GetString());
        Assert.Equal(ModelScopeTelemetry.AuditWriteFailureMetricName, metrics.GetProperty("auditWriteFailures").GetProperty("otelName").GetString());

        var labels = root.GetProperty("boundedLabels").EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidDataException("A bounded label cannot be null."))
            .ToArray();
        Assert.Equal(["modelscope_runtime", "modelscope_task", "modelscope_streaming", "modelscope_outcome"], labels);
        Assert.DoesNotContain("fingerprint", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DashboardAndAlertsReferenceEveryExportedMetricAndAuditFailureIsCritical()
    {
        using var contract = JsonDocument.Parse(File.ReadAllText(Asset("metric-contract.json")));
        using var dashboard = JsonDocument.Parse(File.ReadAllText(Asset("grafana-dashboard.json")));
        var dashboardText = dashboard.RootElement.GetRawText();
        var rules = File.ReadAllText(Asset("prometheus-rules.yaml"));
        var metrics = contract.RootElement.GetProperty("metrics");

        foreach (var metric in metrics.EnumerateObject())
        {
            var prometheusName = metric.Value.GetProperty("prometheusName").GetString()!;
            var queryName = metric.Value.GetProperty("type").GetString() == "histogram"
                ? prometheusName + "_bucket"
                : prometheusName;
            Assert.Contains(queryName, dashboardText, StringComparison.Ordinal);
            Assert.Contains(queryName, rules, StringComparison.Ordinal);
        }

        Assert.Contains("alert: ModelScopeAuditWritesFailing", rules, StringComparison.Ordinal);
        Assert.Contains("severity: critical", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("model_fingerprint", dashboardText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model_fingerprint", rules, StringComparison.OrdinalIgnoreCase);
    }

    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "observability", name);
}
