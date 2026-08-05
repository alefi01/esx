/*
 * Выбор светлой или тёмной темы.
 *
 * Этот файл подключается в <head>, ДО отрисовки страницы, и намеренно
 * отделён от основного app.js. Причина: если применить тему после того,
 * как страница уже нарисована, человек увидит вспышку — сначала светлый
 * фон, потом тёмный. Выглядит как неисправность.
 *
 * Состояний два, светлая и тёмная, — ровно как в макете. При первом заходе
 * берётся та, что стоит в системе; дальше действует выбор человека.
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
            var saved = localStorage.getItem(KEY);

            if (saved === 'light' || saved === 'dark') {
                return saved;
            }
        } catch (error) {
            // localStorage может быть отключён политиками браузера.
        }

        // Ещё не выбирали — берём настройку системы.
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
            ? 'dark'
            : 'light';
    }

    function apply(mode) {
        // Тема ставится на <html>, а не на <body>, как в макете: <body>
        // в этот момент ещё не существует — скрипт выполняется в <head>.
        // Стили принимают оба варианта, см. site.css.
        document.documentElement.setAttribute('data-theme', mode);

        // Подсказка браузеру: от неё зависит вид полос прокрутки
        // и стандартных элементов управления.
        var meta = document.querySelector('meta[name="color-scheme"]');

        if (meta) {
            meta.setAttribute('content', mode);
        }
    }

    apply(stored());

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

        /** Переключение на противоположную. Возвращает новую тему. */
        toggle: function () {
            var next = stored() === 'dark' ? 'light' : 'dark';

            this.set(next);

            return next;
        }
    };
})();
