using ReversibleActionCompensation.AgentFramework;

var effects = new List<string>();
var saga = new SagaRunner();

CompensableStep[] checkout =
[
    new("Reserve inventory",
        key => effects.Add($"reserved ({key})"),
        key => effects.Add($"released ({key})")),
    new("Charge card",
        key => effects.Add($"charged ({key})"),
        key => effects.Add($"refunded ({key})")),
    new("Create shipping label",
        _ => throw new InvalidOperationException("carrier unavailable"),
        _ => { })
];

var result = saga.Run("checkout-1042", checkout);

Console.WriteLine($"Saga: {result.Status}");
foreach (var item in result.Events)
    Console.WriteLine($"  {item.Phase,-10} {item.Step,-22} {(item.Succeeded ? "ok" : item.Error)}");

Console.WriteLine("\nSide effects show reverse-order compensation:");
foreach (var effect in effects)
    Console.WriteLine($"  {effect}");

var replay = saga.Run("checkout-1042", checkout);
Console.WriteLine($"\nRetry returned the recorded {replay.Status} result; no effects ran twice.");
