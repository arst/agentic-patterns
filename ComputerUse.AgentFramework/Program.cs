using System.Text.Json;
using ComputerUse.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared;

var desktop = new VirtualDesktop();
var operatorAgent = new ChatClientAgent(Settings.ChatClient, name: "ComputerOperator",
    instructions: "Operate only from the latest screenshot. Return one click on the visible element that best advances the goal. Never invent coordinates.");
var precise = new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0f });

var result = await new ComputerUseRunner().RunAsync(
    desktop,
    async (screenshot, _) => (await operatorAgent.RunAsync<GuiAction>($"""
        Goal: enable dark mode.
        Screenshot: {JsonSerializer.Serialize(screenshot)}
        Choose one click.
        """, options: precise)).Result,
    screenshot => screenshot.DarkMode,
    maxSteps: 6);

foreach (var step in result.Steps)
    Console.WriteLine($"{step.Before.Screen} popup={step.Before.PopupOpen} -> click({step.Action.X},{step.Action.Y}) -> {step.Message}");

Console.WriteLine(result.Completed ? "Goal reached: dark mode is on." : "Stopped at the host step limit.");
