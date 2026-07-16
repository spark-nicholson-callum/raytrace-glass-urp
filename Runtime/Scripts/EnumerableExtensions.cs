using System;
using System.Collections.Generic;

public static class EnumerableExtensions
{
    public static IEnumerable<TResult> ZipLong<TFirst, TSecond, TResult>(
        this IEnumerable<TFirst> first,
        IEnumerable<TSecond> second,
        Func<TFirst, TSecond, TResult> selector,
        TFirst defaultFirst = default,
        TSecond defaultSecond = default
    ) {
        using var e1 = first.GetEnumerator();
        using var e2 = second.GetEnumerator();

        bool hasNext1 = e1.MoveNext();
        bool hasNext2 = e2.MoveNext();

        while (hasNext1 || hasNext2)
        {
            var item1 = hasNext1 ? e1.Current : defaultFirst;
            var item2 = hasNext2 ? e2.Current : defaultSecond;

            yield return selector(item1, item2);

            if (hasNext1) hasNext1 = e1.MoveNext();
            if (hasNext2) hasNext2 = e2.MoveNext();
        }
    }
}
