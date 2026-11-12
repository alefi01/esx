using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Shared;

/// <summary>
/// Данные для карточки объявления. Ровно то, что нужно разметке,
/// и ничего сверх: страница не должна сама решать, кто что может.
/// </summary>
/// <param name="Item">Само объявление вместе с вложениями.</param>
/// <param name="CanModify">Показывать ли кнопки правки и удаления.</param>
/// <param name="Views">Сколько человек прочитало. Ноль — значок всё равно показываем.</param>
/// <param name="Role">
/// Роль папки: «общая» или «внутренняя работа». От неё зависит подпись
/// контура — круг читателей у объявления задаётся именно папкой.
/// </param>
public sealed record AnnouncementCard(
    Announcement Item, bool CanModify, int Views = 0, string? Role = null)
{
    /// <summary>
    /// В каком контуре висит объявление.
    ///
    /// Автор должен видеть, кому он пишет, а читатель — понимать,
    /// можно ли пересказывать прочитанное за пределами отдела.
    /// Без подписи это знание держится на том, в какую папку человек
    /// случайно зашёл.
    /// </summary>
    public string? Contour => Role switch
    {
        DepartmentRoles.Public => "Общий контур — видят все сотрудники",
        DepartmentRoles.Internal => "Внутренний контур — видит только отдел",
        _ => null
    };

    public bool IsInternal => Role == DepartmentRoles.Internal;

    /// <summary>
    /// Буквы для кружка вместо фотографии. Фотографий в Active Directory
    /// обычно нет, а кружок с инициалами читается лучше, чем безликий значок.
    /// </summary>
    public string Initials
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Item.AuthorDisplayName)
                ? Item.AuthorUserName
                : Item.AuthorDisplayName;

            var parts = name.Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                return "?";
            }

            return parts.Length == 1
                ? parts[0][..1].ToUpperInvariant()
                : (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
        }
    }

    public string FormatSize(long bytes) => UploadValidator.Format(bytes);
}
