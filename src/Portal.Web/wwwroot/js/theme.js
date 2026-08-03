/*
 * Выбор светлой или тёмной темы.
 *
 * Этот файл подключается в <head>, ДО отрисовки страницы, и намеренно
 * отделён от основного app.js. Причина: если применить тему после того,
 * как страница уже нарисована, человек увидит вспышку — сначала светлый
 * фон, потом тёмный. Выглядит как неисправность.
 *
 * Возможны три состояния:
 *   auto  — как в системе (значение по умолчанию)
 *   light — всегда светлая
 *   dark  — всегда тёмная
 *
 * Выбор запоминается в localStorage, то есть отдельно на каждом компьютере
 * и в каждом браузере. На сервер он не отправляется: это личная настройка
 * внешнего вида, базе про неё знать незачем.
 */

(function () {
    'use strict';

    var KEY = 'portal.theme';

    function stored() {
        try {
            return localStorage.getItem(KEY) || 'auto';
        } catch (error) {
            // localStorage может быть отключён политиками браузера —
            // тогда просто работаем в системном режиме.
            return 'auto';
        }
    }

    function apply(mode) {
        var root = document.documentElement;

        if (mode === 'auto') {
            root.removeAttribute('data-theme');
        } else {
            root.setAttribute('data-theme', mode);
        }

        // Подсказка браузеру: от неё зависит вид полос прокрутки
        // и стандартных элементов управления.
        var meta = document.querySelector('meta[name="color-scheme"]');

        if (meta) {
            meta.setAttribute('content', mode === 'auto' ? 'light dark' : mode);
        }
    }

    apply(stored());

    // Наружу отдаём минимум: текущий режим, переключение и применение.
    // Основной код страницы пользуется этим для кнопки в шапке.
    window.portalTheme = {
        current: stored,

        set: function (mode) {
            try {
                localStorage.setItem(KEY, mode);
            } catch (error) {
                // Не смогли запомнить — тема всё равно применится до перезагрузки.
            }

            apply(mode);
        },

        /** Перебор по кругу: как в системе → светлая → тёмная → как в системе. */
        cycle: function () {
            var next = { auto: 'light', light: 'dark', dark: 'auto' }[stored()] || 'auto';

            this.set(next);

            return next;
        }
    };
})();
