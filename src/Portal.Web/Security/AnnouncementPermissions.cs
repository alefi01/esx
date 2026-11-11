using System.Security.Claims;
using Portal.Web.Data;

namespace Portal.Web.Security;

/// <summary>
/// Правило «кто что может делать с объявлением».
///
/// Вынесено в отдельное место специально: одно и то же правило нужно
/// и странице редактирования (пустить или нет), и странице ленты
/// (показывать кнопку «Изменить» или нет). Если написать его в двух местах,
/// однажды они разойдутся — и кнопка будет показываться там, где действие
/// на самом деле запрещено.
///
/// Правило простое:
///   * автор может править и удалять своё;
///   * администратор портала — любое;
///   * остальные — ничего, даже если состоят в группе публикаторов.
///
/// То есть членство в группе публикаторов даёт право ПИСАТЬ СВОЁ,
/// а не редактировать чужое.
/// </summary>
public static class AnnouncementPermissions
{
    public static bool CanModify(ClaimsPrincipal user, Announcement announcement, string adminGroup)
    {
        if (user.IsInRole(adminGroup))
        {
            return true;
        }

        var userName = user.Identity?.Name;

        return !string.IsNullOrEmpty(userName)
               && string.Equals(userName, announcement.AuthorUserName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Можно ли вешать объявления на эту папку.
    ///
    /// Объявления ведёт начальник отдела — того самого, которому папка
    /// принадлежит. Не «издатель вообще» и не всякий, кто управляет
    /// папкой: объявление отдела подписано отделом, и отвечает за него
    /// тот, кого назначили начальником.
    ///
    /// Администратор портала может везде — как и во всём остальном.
    /// </summary>
    public static bool CanPublishIn(
        ClaimsPrincipal user,
        StorageFolder folder,
        Portal.Web.Services.Storage.FolderTree tree,
        string adminGroup)
    {
        // Вне отдела объявлений нет вовсе: вешать их на обычную папку
        // значило бы заводить ленту там, где никто её не ищет.
        if (tree.DepartmentOf(folder) is null)
        {
            return false;
        }

        return user.IsInRole(adminGroup) || tree.IsHead(user, folder);
    }

    /// <summary>
    /// Можно ли править и удалять это объявление: начальник отдела,
    /// его автор или администратор.
    /// </summary>
    public static bool CanModify(
        ClaimsPrincipal user,
        Announcement announcement,
        Portal.Web.Services.Storage.FolderTree tree,
        string adminGroup)
    {
        if (CanModify(user, announcement, adminGroup))
        {
            return true;
        }

        if (announcement.FolderId is not { } folderId)
        {
            return false;
        }

        var folder = tree.Get(folderId);

        return folder is not null && tree.IsHead(user, folder);
    }
}
