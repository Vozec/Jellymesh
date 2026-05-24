using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Federation.Services;

public class LocalCatalogDigest
{
    private readonly ILibraryManager _libraryManager;

    public LocalCatalogDigest(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public DigestSnapshot Compute()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            Recursive = true
        };

        var ids = new List<(string Id, DateTime Modified)>();
        foreach (var item in _libraryManager.GetItemList(query))
        {
            ids.Add((item.Id.ToString("N"), item.DateLastSaved));
        }

        ids.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        var sb = new StringBuilder(ids.Count * 36);
        DateTime? latest = null;
        foreach (var t in ids)
        {
            sb.Append(t.Id).Append(':').Append(t.Modified.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (latest is null || t.Modified > latest) latest = t.Modified;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return new DigestSnapshot(ids.Count, hash, latest);
    }

    public IReadOnlyList<CatalogItemRef> List()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            Recursive = true
        };
        return _libraryManager.GetItemList(query)
            .Select(i => new CatalogItemRef(i.Id.ToString("N"), i.Name, i.GetType().Name, i.DateLastSaved))
            .ToList();
    }
}

public record DigestSnapshot(int Count, string Hash, DateTime? LatestModifiedUtc);
public record CatalogItemRef(string Id, string Name, string Type, DateTime ModifiedUtc);
