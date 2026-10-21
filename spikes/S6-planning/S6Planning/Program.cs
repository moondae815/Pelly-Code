namespace S6Planning;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.Error.WriteLine("usage: index <caseId> | run --arm A|B --repeat N [--case id] | callers | score | report");
        return 2;
    }
}
