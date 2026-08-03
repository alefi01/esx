using System.Text;

namespace Portal.Web.Services.ActiveDirectory;

/// <summary>
/// Экранирование значений, которые мы подставляем в LDAP-фильтры.
///
/// Зачем это нужно. LDAP-фильтр — это строка вида (&amp;(objectClass=user)(sAMAccountName=ivanov)).
/// Если подставить в неё логин из формы как есть, пользователь может ввести
/// «*» и получить фильтр (sAMAccountName=*), который совпадёт с любой учёткой,
/// или «)(objectClass=*» и сломать структуру запроса. Это LDAP-инъекция —
/// прямой аналог SQL-инъекции. Поэтому спецсимволы заменяем на \XX (шестнадцатеричный код).
///
/// Правила — RFC 4515, раздел 3.
/// </summary>
public static class LdapEscaper
{
    /// <summary>
    /// Экранирует произвольное значение для подстановки в LDAP-фильтр.
    /// Применять к КАЖДОМУ значению, пришедшему извне, включая DN пользователя:
    /// DN вполне может содержать запятые и обратные слэши (CN=Иванов\, Иван).
    /// </summary>
    public static string EscapeFilterValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\5c"); break;
                case '*': builder.Append("\\2a"); break;
                case '(': builder.Append("\\28"); break;
                case ')': builder.Append("\\29"); break;
                case '\0': builder.Append("\\00"); break;
                case '/': builder.Append("\\2f"); break; // не по RFC, но AD на «/» в фильтре ругается
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }
}
