namespace PellyCoder.Core.Specs.Drafting;

/// <summary>
/// The frozen planning prompt of the S6 probe (spikes/S6-planning/S6Planning/PlanPrompt.cs). The wording must stay
/// identical to S6's <c>System</c>; a test compares the two.
/// </summary>
public static class DraftPrompt
{
    public const string System =
        """
        You split a change request into tasks for a tool that edits exactly one C# member or one SQL file per task.
        Output only task lines in this grammar, one per line:
        - [ ] T<n> <cs|sql> <path>[#Member] <what to change> [G<k>]
        Rules:
        1. Use only paths and members that appear in the index. Copy paths exactly as written there.
        2. A cs task must name one member after '#'. A sql task names the file.
        3. If a member's signature changes, put that task and every task needed for the code to build again in the same group [G1].
        4. List a changed declaration before the code that uses it.
        5. Do not add tasks the request does not need.

        Example request: Let a loan be extended by a given number of days, also in the database procedure.
        Example answer:
        - [ ] T1 cs Library/ILoanService.cs#Extend add an int days parameter [G1]
        - [ ] T2 cs Library/LoanService.cs#Extend add the int days parameter and use it for the new due date [G1]
        - [ ] T3 cs Library/LoanController.cs#Extend pass the requested days to Extend [G1]
        - [ ] T4 sql Db/usp_ExtendLoan.sql add @Days int and use it in the UPDATE
        """;

    /// <summary>Names that only occur in the prompt's example; the S6 probe used them to detect copied example lines.</summary>
    public static readonly IReadOnlyList<string> ExampleNames = ["LoanService", "ILoanService", "usp_ExtendLoan", "Library/"];

    public static string User(string request, string renderedIndex) => "Index:\n" + renderedIndex + "\nRequest:\n" + request;

    /// <summary>Largest first input (system + index + request) in tokens; the caller refuses to call the model beyond it.</summary>
    public const int InputBudget = 4096;
}
