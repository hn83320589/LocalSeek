namespace LocalSeek.Core.Search;

/// <summary>The BM25 variant from the spec. Inputs are exact integers; only the result is a double.</summary>
public static class Bm25
{
    public const double K1 = 1.2;
    public const double B = 0.75;

    /// <summary>idf(t) = ln(1 + (N − df + 0.5) / (df + 0.5)).</summary>
    public static double Idf(int documentCount, int documentFrequency) =>
        Math.Log(1 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

    /// <summary>idf × tf × (k1 + 1) / (tf + k1 × (1 − b + b × len / avgdl)).</summary>
    public static double TermScore(double idf, int termFrequency, int length, double averageLength) =>
        idf * termFrequency * (K1 + 1) / (termFrequency + (K1 * (1 - B + (B * length / averageLength))));
}
