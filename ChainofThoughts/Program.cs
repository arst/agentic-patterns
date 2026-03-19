using Microsoft.SemanticKernel.ChatCompletion;
using Shared;

var cotKernel = Settings.Kernel;
var cotService = cotKernel.GetRequiredService<IChatCompletionService>();

var cotHistory = new ChatHistory();
cotHistory.AddSystemMessage("""
                            You are an analytical reasoning agent. For every question:

                            1. **Analyze**: Identify the core problem and key variables.
                            2. **Decompose**: Break the problem into smaller sub-problems.
                            3. **Reason through each step**: Solve each sub-problem, showing your work.
                            4. **Synthesize**: Combine the sub-answers into a final conclusion.
                            5. **Verify**: Check your answer for logical consistency and errors.

                            Always show your complete reasoning process before giving the final answer.
                            Format: use "Step N:" headers for each reasoning step, then "Final Answer:" for the conclusion.
                            """);

cotHistory.AddUserMessage(
    "A store offers 20% off, then an additional 15% off the sale price. " +
    "Is this the same as a single 35% discount? Explain with a $100 item.");

var cotResponse = await cotService.GetChatMessageContentAsync(cotHistory);
Console.WriteLine($"CoT Agent:\n{cotResponse.Content}\n");