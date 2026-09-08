using ModelScope.Net.Runtime;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/ModelScope.Net.Sample -- MODEL_DIRECTORY");
    return 2;
}

var capabilities = await new ModelInspector().InspectAsync(args[0]);
Console.WriteLine($"Model: {capabilities.ModelId ?? capabilities.ModelPath}");
Console.WriteLine($"Task: {capabilities.Task ?? "unknown"}");
foreach (var candidate in capabilities.RuntimeCandidates)
{
    Console.WriteLine($"{candidate.RuntimeName}: {candidate.Status} - {candidate.Reason}");
}

return 0;
