using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Files;

/// <summary>
/// Избранное: файлы и папки, которые человек отметил звёздочкой.
///
/// Отметка сама по себе доступа НЕ даёт. Права проверяются здесь заново,
/// по дереву папок, и то, к чему доступ пропал, в список просто не попадает.
/// Иначе избранное со временем стало бы обходным путём к закрытым папкам:
/// отметил, пока пускали, — видишь и после того, как перестали.
/// </summary>
public class FavoritesModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly FolderTree _tree;

    public FavoritesModel(PortalDbContext db, FolderTree tree)
    {
        _db = db;
        _tree = tree;
    }

    /// <summary>Отмеченная папка вместе с путём до неё.</summary>
    public sealed record FolderItem(StorageFolder Folder, string Path);

    /// <summary>Отмеченный файл вместе с папкой, в которой лежит.</summary>
    public sealed record FileItem(StoredFile File, string FolderPath, int FolderId);

    public IReadOnlyList<FolderItem> Folders { get; private set; } = [];
    public IReadOnlyList<FileItem> Files { get; private set; } = [];

    /// <summary>Список для показа — тот же тип, что и в разделе «Файлы».</summary>
    public IReadOnlyList<Portal.Web.Pages.Shared.FileEntry> Entries { get; private set; } = [];

    public bool IsEmpty => Entries.Count == 0;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var name = User.Identity?.Name ?? "";

        // Свежие отметки сверху: то, что отметили только что, обычно
        // и нужно прямо сейчас.
        var marks = await _db.Favorites
            .Where(f => f.UserName == name)
            .OrderByDescending(f => f.AddedAt)
            .ToListAsync(cancellationToken);

        var fileIds = marks.Where(m => m.FileId is not null).Select(m => m.FileId!.Value).ToList();

        // Удалённые в корзину не показываем: файл ещё существует, но человек
        // его выбросил, и в избранном ему делать нечего.
        var files = fileIds.Count == 0
            ? []
            : await _db.Files
                .Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null)
                .ToListAsync(cancellationToken);

        var byId = files.ToDictionary(f => f.Id);

        var folderItems = new List<FolderItem>();
        var fileItems = new List<FileItem>();

        foreach (var mark in marks)
        {
            if (mark.FolderId is { } folderId)
            {
                var folder = _tree.Get(folderId);

                if (folder is not null && _tree.IsVisible(User, folder))
                {
                    folderItems.Add(new FolderItem(folder, _tree.DisplayPath(folder)));
                }

                continue;
            }

            if (mark.FileId is { } fileId && byId.TryGetValue(fileId, out var file))
            {
                var parent = _tree.Get(file.FolderId);

                if (parent is not null && _tree.CanRead(User, parent))
                {
                    fileItems.Add(new FileItem(file, _tree.DisplayPath(parent), parent.Id));
                }
            }
        }

        Folders = folderItems;
        Files = fileItems;

        var entries = new List<Portal.Web.Pages.Shared.FileEntry>();

        var childCounts = await _tree.ChildCountsAsync(
            User, folderItems.Select(i => i.Folder).ToList(), cancellationToken);

        foreach (var item in folderItems)
        {
            entries.Add(Portal.Web.Pages.Shared.FileEntry.ForFolder(
                item.Folder,
                Url.Page("Index", new { id = item.Folder.Id }) ?? "#",
                childCounts.GetValueOrDefault(item.Folder.Id),
                favorite: true,
                canManage: _tree.CanManage(User, item.Folder),
                path: item.Path));
        }

        foreach (var item in fileItems)
        {
            entries.Add(Portal.Web.Pages.Shared.FileEntry.ForFile(
                item.File,
                Url.Page("Index", "Download", new { fileId = item.File.Id }) ?? "#",
                IndexModel.PreviewKindOf(item.File),
                favorite: true,
                canDelete: false,
                path: item.FolderPath));
        }

        Entries = entries;
    }

    public static string FormatSize(long bytes) => UploadValidator.Format(bytes);

    public static string FileKindOf(StoredFile file) => FileKinds.Of(file.OriginalName);

    public static string ExtensionOf(StoredFile file) => IndexModel.ExtensionOf(file);
}
