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
}
