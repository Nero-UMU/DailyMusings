using DailyMusings.Domain.Common;

namespace DailyMusings.Domain.Retrieval;

/// <summary>
/// Vector arithmetic for semantic retrieval (docs/开发指导.md §8.3).
/// <para>
/// The distance measure is cosine similarity, which is what embedding endpoints are built around, mapped into
/// <c>[0,1]</c> because relevance elsewhere in the product — <c>SourceReference.Relevance</c>, the retrieval
/// thresholds — is expressed as a fraction. Negative similarity means "unrelated or opposed" and is reported
/// as zero rather than as a negative relevance a caller would have to special-case.
/// </para>
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity in <c>[0,1]</c>. Vectors of different lengths are not comparable — two models produce
    /// dimensions that cannot be mixed — so that is refused rather than silently truncated.
    /// </summary>
    public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new DomainException(
                "vector.dimension_mismatch",
                $"Vectors of {left.Length} and {right.Length} dimensions cannot be compared.");
        }

        if (left.Length == 0)
        {
            return 0;
        }

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }

        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0; // a zero vector has no direction to compare
        }

        var similarity = dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        return double.IsNaN(similarity) ? 0 : Math.Clamp(similarity, 0, 1);
    }
}
