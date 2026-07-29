internal static class Log
{
    public static void Info(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();
    }
}
