using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Shared;

/// <summary>
/// Данные для карточки объявления. Ровно то, что нужно разметке,
/// и ничего сверх: страница не должна сама решать, кто что может.
/// </summary>
/// <param name="Item">Само объявление вместе с вложениями.</param>
/// <param name="CanModify">Показывать ли кнопки правки и удаления.</param>
public sealed record AnnouncementCard(Announcement Item, bool CanModify)
{
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
