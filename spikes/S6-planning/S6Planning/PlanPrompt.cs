using PellyCoder.Core.Llm;

namespace S6Planning;

/// <summary>
/// The single, frozen planning prompt (design §5). The example uses a lending-library domain that shares no names with
/// the corpus, so copied example names can be detected in outputs (<see cref="ExampleNames"/>).
/// </summary>
public static class PlanPrompt
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

    public static readonly string[] ExampleNames = ["LoanService", "ILoanService", "usp_ExtendLoan", "Library/"];

    public static string User(string request, CorpusIndex index) => "Index:\n" + index.Render() + "\nRequest:\n" + request;

    public static string Raw(QwenChatTemplate t, string request, CorpusIndex index, bool think) =>
        t.Build(System, User(request, index)) + (think ? t.ThinkOpen : t.EmptyThink);

    public static int CountTokens(string raw) => QwenTokenCounter.Shared.Count(raw);
}
