/*
 * Оформление портала: тема и фон рабочего пространства.
 *
 * Этот файл подключается в <head>, ДО отрисовки страницы, и намеренно
 * отделён от основного app.js. Причина: если применить тему после того,
 * как страница уже нарисована, человек увидит вспышку — сначала светлый
 * фон, потом тёмный. Выглядит как неисправность.
 *
 * ДВЕ НЕЗАВИСИМЫЕ НАСТРОЙКИ
 *
 * Тема (светлая/тёмная) задаёт цвета: текст, поля, карточки, границы.
 * Фон — только картинку под содержимым рабочей области. Они не связаны:
 * любой фон работает и на светлой теме, и на тёмной, и выбирать их можно
 * по отдельности. Слить их в один список «семь тем» значило бы завести
 * семь наборов цветов и поддерживать каждый.
 *
 * Картинка размывается и притеняется полупрозрачной пеленой — иначе
 * поверх фотографии не читается ни один мелкий текст, а его в портале
 * почти весь. Размытие делает стиль, здесь только адрес картинки.
 *
 * Выбор запоминается в localStorage, то есть отдельно на каждом компьютере
 * и в каждом браузере. На сервер он не отправляется: это личная настройка
 * внешнего вида, базе про неё знать незачем.
 */

(function () {
    'use strict';

    var KEY = 'portal.theme';
    var BG_KEY = 'portal.background';
    var BG_URL_KEY = 'portal.backgroundUrl';

    function read(key) {
        try {
            return localStorage.getItem(key);
        } catch (error) {
            // localStorage может быть отключён политиками браузера.
            return null;
        }
    }

    function write(key, value) {
        try {
            if (value) {
                localStorage.setItem(key, value);
            } else {
                localStorage.removeItem(key);
            }
        } catch (error) {
            // Не смогли запомнить — выбор всё равно подействует до перезагрузки.
        }
    }

    function stored() {
        var saved = read(KEY);

        if (saved === 'light' || saved === 'dark') {
            return saved;
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

    /**
     * Ставит картинку под рабочую область.
     *
     * Слой и картинка появляются в разметке только вместе с <body>,
     * поэтому здесь, в <head>, вызов ничего не найдёт и тихо ничего
     * не сделает; app.js повторит вызов, когда страница нарисована.
     */
    function applyBackground(url) {
        var layer = document.getElementById('bgLayer');
        var image = document.getElementById('bgImage');

        if (!layer || !image) {
            return;
        }

        if (url) {
            // Адрес идёт в CSS, поэтому кавычки и скобки из него убираем:
            // имя файла задаёт администратор, но в разметку оно попадать
            // как есть не должно.
            image.style.backgroundImage = 'url("' + url.replace(/["'()\\]/g, '') + '")';
            layer.hidden = false;
        } else {
            image.style.backgroundImage = '';
            layer.hidden = true;
        }
    }

    apply(stored());

    window.portalTheme = {
        current: stored,

        set: function (mode) {
            write(KEY, mode);
            apply(mode);
        },

        /** Переключение на противоположную. Возвращает новую тему. */
        toggle: function () {
            var next = stored() === 'dark' ? 'light' : 'dark';

            this.set(next);

            return next;
        },

        /** Выбранный фон: имя файла либо пустая строка. */
        background: function () {
            return read(BG_KEY) || '';
        },

        /**
         * Запоминает и показывает фон.
         *
         * Адрес хранится рядом с именем файла: без него после перезагрузки
         * пришлось бы искать картинку по имени в списке, а список приходит
         * с сервером и на странице входа его нет вовсе.
         */
        setBackground: function (id, url) {
            write(BG_KEY, id);
            write(BG_URL_KEY, id ? url : '');

            applyBackground(id ? url : '');
        },

        /** Показывает запомненный фон. Вызывается, когда страница нарисована. */
        restoreBackground: function () {
            applyBackground(read(BG_KEY) ? read(BG_URL_KEY) : '');
        }
    };
})();
