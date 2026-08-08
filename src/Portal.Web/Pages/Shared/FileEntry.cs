using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Shared;

/// <summary>
/// Одна строка списка файлов — папка или файл, всё равно.
///
/// Зачем общий тип: список показывается в четырёх местах (папка, поиск,
/// избранное, корзина) и в двух видах (плитками и строками). Разметка
/// у всех одна, поэтому и данные для неё удобнее иметь одни. Иначе каждый
/// шаблон лез бы в свою сущность и они неминуемо разъехались бы.
///
/// Здесь ТОЛЬКО то, что нужно разметке. Решения «можно ли удалить»,
/// «в избранном ли» принимает страница, а не шаблон: шаблон не должен
/// сам разбираться в правах — это верный способ однажды показать лишнее.
/// </summary>
/// <param name="Id">Ключ для кода страницы: «f12» — файл, «d3» — папка.</param>
/// <param name="Name">Что видит человек.</param>
/// <param name="Href">Куда ведёт двойное нажатие: открыть папку или скачать файл.</param>
/// <param name="PreviewKind">Чем показывать файл. Пусто — показать нельзя, только скачать.</param>
/// <param name="TypeName">Название типа для столбца «Тип».</param>
/// <param name="Meta">Подпись под именем в плитке: «2,4 МБ · 5 авг 2026» либо «7 элем.».</param>
/// <param name="Pinned">Закреплено наверху каталога — общей отметкой, а не личной звёздочкой.</param>
/// <param name="CanPin">
/// Показывать ли кнопку закрепления. Отличается от <paramref name="CanManage"/>
/// у уже закреплённого: снять чужое закрепление может только тот, кто его
/// поставил, либо администратор портала.
/// </param>
public sealed record FileEntry(
    string Id,
    int? FileId,
    int? FolderId,
    string Name,
    string Href,
    string PreviewKind,
    string TypeName,
    string Meta,
    string SizeText,
    DateTime At,
    bool IsFolder,
    bool Favorite,
    bool CanDelete,
    bool CanManage,
    string? Path = null,
    bool Pinned = false,
    bool CanPin = false)
{
    /// <summary>
    /// Папка в списке.
    ///
    /// Объём показывается не всем: размер папки — косвенный признак того,
    /// что в ней лежит. Кому его видно, решает страница (см. ShowSizes),
    /// и передаёт сюда уже готовое значение либо null.
    ///
    /// В childCount входят И подпапки, И файлы. Считать одни подпапки нельзя:
    /// папка с двумя десятками документов, но без вложенных папок, подписывалась
    /// бы «пусто» — прямая неправда, из-за которой в неё не заходят.
    /// </summary>
    public static FileEntry ForFolder(
        StorageFolder folder, string href, int childCount, bool favorite, bool canManage,
        long? sizeBytes = null, string? path = null, bool canPin = false) =>
        new(
            Id: "d" + folder.Id,
            FileId: null,
            FolderId: folder.Id,
            Name: folder.Name,
            Href: href,
            PreviewKind: "",
            TypeName: "Папка",
            Meta: (childCount == 0 ? "пусто" : childCount + " элем.")
                  + (sizeBytes is { } bytes and > 0 ? " · " + UploadValidator.Format(bytes) : ""),
            SizeText: sizeBytes is { } size and > 0 ? UploadValidator.Format(size) : "—",
            At: folder.CreatedAt,
            IsFolder: true,
            Favorite: favorite,
            CanDelete: false,
            CanManage: canManage,
            Path: path,
            Pinned: folder.IsPinned,
            CanPin: canPin);

    /// <summary>Файл в списке.</summary>
    public static FileEntry ForFile(
        StoredFile file, string href, string previewKind, bool favorite, bool canDelete,
        string? path = null, bool canManage = false, bool canPin = false) =>
        new(
            Id: "f" + file.Id,
            FileId: file.Id,
            FolderId: null,
            Name: file.OriginalName,
            Href: href,
            PreviewKind: previewKind,
            TypeName: FileIcons.Describe(file.OriginalName),
            Meta: UploadValidator.Format(file.SizeBytes) + " · " + file.UploadedAt.ToLocalTime().ToString("dd.MM.yyyy"),
            SizeText: UploadValidator.Format(file.SizeBytes),
            At: file.UploadedAt,
            IsFolder: false,
            Favorite: favorite,
            CanDelete: canDelete,
            CanManage: canManage,
            Path: path,
            Pinned: file.IsPinned,
            CanPin: canPin);
}
