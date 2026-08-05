/*
 * Код страниц портала.
 *
 * Написан на обычном JavaScript, без единой библиотеки. Причина та же,
 * что и у стилей: в вашей сети нет интернета, а значит нет ни npm,
 * ни возможности подтянуть что-то с CDN. Зато этот файл можно прочитать
 * целиком за один присест и поправить, не изучая чужой фреймворк.
 *
 * Файл подключается один раз в общей разметке (_Layout.cshtml) и сам
 * определяет, что на текущей странице есть, а чего нет.
 *
 * Содержание:
 *   1. Мелкие помощники
 *   2. Полоса загрузки страницы
 *   3. Всплывающие сообщения
 *   4. Модальные окна
 *   5. Контекстное меню
 *   6. Панель предпросмотра
 *   7. Файловый менеджер: выделение, буфер обмена, перетаскивание, загрузка
 *   8. Переключатель светлой и тёмной темы
 *   9. Колокольчик уведомлений
 *  10. Переписки
 *  11. Сворачивание длинных объявлений
 */

(function () {
    'use strict';

    // ======================================================================
    // 1. Мелкие помощники
    // ======================================================================

    const $ = (selector, root) => (root || document).querySelector(selector);
    const $$ = (selector, root) => Array.from((root || document).querySelectorAll(selector));

    /** Человекочитаемый размер: 12,3 МБ вместо 12897485. */
    function formatSize(bytes) {
        const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
        let value = bytes;
        let unit = 0;

        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }

        return unit === 0
            ? bytes + ' ' + units[unit]
            : value.toFixed(1).replace('.', ',').replace(',0', '') + ' ' + units[unit];
    }

    /**
     * Токен защиты форм от подделки. Берётся из любой формы на странице:
     * ASP.NET Core кладёт его в каждую скрытым полем, а без него сервер
     * отклонит наш запрос — и правильно сделает.
     */
    function antiforgeryToken() {
        const field = $('input[name="__RequestVerificationToken"]');
        return field ? field.value : '';
    }

    /** Отправляет скрытую форму, добавив в неё нужные поля. */
    function submitForm(form, fields) {
        if (!form) {
            return;
        }

        // Убираем поля, добавленные при прошлой отправке.
        $$('[data-temp]', form).forEach(node => node.remove());

        Object.keys(fields).forEach(name => {
            const values = Array.isArray(fields[name]) ? fields[name] : [fields[name]];

            values.forEach(value => {
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = name;
                input.value = value;
                input.dataset.temp = '1';
                form.appendChild(input);
            });
        });

        progress.start();
        form.submit();
    }

    // ======================================================================
    // 2. Полоса загрузки страницы
    //
    // Тонкая полоска сверху, пока страница переходит по ссылке или
    // отправляет форму. Нужна не для красоты: на канале между офисами
    // ответ может идти секунду-две, и без отклика человек жмёт кнопку
    // второй раз.
    // ======================================================================

    // ----------------------------------------------------------------------
    // Полосы заполнения (место в папке, место в хранилище)
    //
    // Ширину полосы задаёт не разметка, а этот код. Причина в политике
    // безопасности страницы (CSP): она запрещает встроенные стили, то есть
    // атрибут style="width: 40%" браузер молча отбрасывает, и полоса
    // выглядела бы всегда одинаково — пустой или полной.
    //
    // Поэтому сервер пишет число в data-percent, а ширину проставляем здесь.
    // ----------------------------------------------------------------------

    $$('[data-percent]').forEach(fill => {
        const percent = parseFloat(fill.dataset.percent);

        fill.style.width = (isNaN(percent) ? 0 : Math.min(100, Math.max(0, percent))) + '%';
    });

    const progress = (function () {
        let bar = null;
        let timer = null;

        function ensure() {
            if (!bar) {
                bar = document.createElement('div');
                bar.className = 'pageprogress';
                document.body.appendChild(bar);
            }
            return bar;
        }

        return {
            start() {
                const element = ensure();
                element.classList.remove('is-done');
                element.classList.add('is-active');

                clearTimeout(timer);
                // Если ответ так и не пришёл, полоска не должна висеть вечно.
                timer = setTimeout(() => this.done(), 30000);
            },
            done() {
                if (!bar) {
                    return;
                }
                clearTimeout(timer);
                bar.classList.remove('is-active');
                bar.classList.add('is-done');
            }
        };
    })();

    // Показываем полоску при обычных переходах и отправке форм.
    document.addEventListener('click', event => {
        const link = event.target.closest('a[href]');

        if (!link || link.target === '_blank' || event.ctrlKey || event.metaKey || event.button !== 0) {
            return;
        }

        // Скачивание файла страницу не меняет — полоска только запутает.
        if (link.href.indexOf('handler=Download') !== -1) {
            return;
        }

        if (link.origin === location.origin) {
            progress.start();
        }
    });

    document.addEventListener('submit', event => {
        progress.start();

        // Кнопка отправки переходит в состояние «занято»: и видно, что
        // нажатие принято, и второй раз нажать уже нельзя.
        const button = event.target.querySelector('button[type="submit"]');

        if (button && event.target.hasAttribute('data-busy-form')) {
            button.classList.add('is-busy');
            button.disabled = true;
        }
    });

    // Возврат по кнопке «назад» отдаёт страницу из кэша — полоску надо убрать.
    window.addEventListener('pageshow', () => progress.done());

    // ======================================================================
    // 3. Всплывающие сообщения
    // ======================================================================

    const toasts = (function () {
        let host = null;

        function ensure() {
            if (!host) {
                host = document.createElement('div');
                host.className = 'toasts';
                document.body.appendChild(host);
            }
            return host;
        }

        return {
            show(text, kind) {
                const toast = document.createElement('div');
                toast.className = 'toast toast--' + (kind || 'info');
                toast.textContent = text;
                ensure().appendChild(toast);

                // Даём браузеру отрисовать элемент, и только потом включаем
                // появление — иначе перехода не будет, элемент сразу окажется на месте.
                requestAnimationFrame(() => toast.classList.add('is-visible'));

                setTimeout(() => {
                    toast.classList.remove('is-visible');
                    setTimeout(() => toast.remove(), 300);
                }, kind === 'error' ? 8000 : 4000);
            }
        };
    })();

    // ======================================================================
    // 4. Модальные окна
    // ======================================================================

    function openModal(name) {
        const modal = $('[data-modal="' + name + '"]');

        if (!modal) {
            return;
        }

        modal.hidden = false;
        requestAnimationFrame(() => modal.classList.add('is-open'));

        const focusTarget = $('[data-autofocus]', modal);

        if (focusTarget) {
            focusTarget.focus();
            focusTarget.select();
        }
    }

    function closeModal(modal) {
        modal.classList.remove('is-open');
        setTimeout(() => { modal.hidden = true; }, 200);
    }

    document.addEventListener('click', event => {
        if (event.target.closest('[data-modal-close]')) {
            const modal = event.target.closest('[data-modal]');
            if (modal) {
                closeModal(modal);
            }
        }
    });

    /**
     * Окно подтверждения вместо системного confirm().
     * Системное окно выглядит по-разному в каждом браузере и выбивается
     * из оформления; здесь же и вид единый, и текст можно написать нормальный.
     * Возвращает обещание: true — подтвердили, false — отказались.
     */
    function confirmDialog(text, okLabel) {
        return new Promise(resolve => {
            const modal = $('[data-modal="confirm"]');

            if (!modal) {
                // Если окна на странице нет, лучше спросить хоть как-то,
                // чем молча выполнить необратимое действие.
                resolve(window.confirm(text));
                return;
            }

            $('[data-confirm-text]', modal).textContent = text;

            const okButton = $('[data-confirm-ok]', modal);
            okButton.textContent = okLabel || 'Удалить';

            let settled = false;

            function finish(result) {
                if (settled) {
                    return;
                }
                settled = true;

                okButton.removeEventListener('click', onOk);
                modal.removeEventListener('click', onCancel);
                closeModal(modal);
                resolve(result);
            }

            function onOk() { finish(true); }

            function onCancel(event) {
                if (event.target.closest('[data-modal-close]')) {
                    finish(false);
                }
            }

            okButton.addEventListener('click', onOk);
            modal.addEventListener('click', onCancel);

            openModal('confirm');
            okButton.focus();
        });
    }

    /**
     * Кнопка, требующая подтверждения, — прямо в разметке.
     *
     * Достаточно повесить на кнопку отправки data-confirm-action и написать
     * текст вопроса в data-confirm-text. Нужно там, где действие необратимо:
     * очистка журнала, удаление и тому подобное.
     */
    document.addEventListener('click', event => {
        const button = event.target.closest('[data-confirm-action]');

        if (!button || button.dataset.confirmed === '1') {
            return;
        }

        event.preventDefault();

        confirmDialog(
            button.dataset.confirmText || 'Выполнить действие?',
            button.dataset.confirmOk || 'Выполнить'
        ).then(ok => {
            if (!ok) {
                return;
            }

            // Второй раз спрашивать не надо: помечаем кнопку и нажимаем
            // её заново, теперь уже по-настоящему.
            button.dataset.confirmed = '1';
            button.click();
        });
    }, true);

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape') {
            const open = $('[data-modal].is-open');
            if (open) {
                closeModal(open);
            }
            hideMenu();
        }
    });

    // ======================================================================
    // 5. Контекстное меню
    // ======================================================================

    const menu = $('[data-context-menu]');

    function hideMenu() {
        if (menu) {
            menu.hidden = true;
            menu.classList.remove('is-open');
        }
    }

    /**
     * Показывает меню в точке нажатия.
     * items: [{ label, icon, danger, onClick }] либо { separator: true }
     */
    function showMenu(x, y, items) {
        if (!menu || items.length === 0) {
            return;
        }

        menu.innerHTML = '';

        items.forEach(item => {
            if (item.separator) {
                const separator = document.createElement('div');
                separator.className = 'menu__sep';
                menu.appendChild(separator);
                return;
            }

            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'menu__item' + (item.danger ? ' menu__item--danger' : '');
            button.setAttribute('role', 'menuitem');

            if (item.icon) {
                const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                svg.setAttribute('class', 'icon');
                const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
                use.setAttribute('href', '#' + item.icon);
                svg.appendChild(use);
                button.appendChild(svg);
            }

            button.appendChild(document.createTextNode(item.label));

            button.addEventListener('click', () => {
                hideMenu();
                item.onClick();
            });

            menu.appendChild(button);
        });

        menu.hidden = false;

        // Сначала показываем, потом измеряем: у скрытого элемента размеров нет.
        const rect = menu.getBoundingClientRect();
        const left = Math.min(x, window.innerWidth - rect.width - 8);
        const top = Math.min(y, window.innerHeight - rect.height - 8);

        menu.style.left = Math.max(8, left) + 'px';
        menu.style.top = Math.max(8, top) + 'px';

        requestAnimationFrame(() => menu.classList.add('is-open'));
    }

    document.addEventListener('click', hideMenu);
    window.addEventListener('resize', hideMenu);
    window.addEventListener('scroll', hideMenu, true);

    // ======================================================================
    // 6. Окно предпросмотра
    //
    // Показывает файл поверх страницы, не уводя с неё человека. Окно лежит
    // в разметке пустым; сюда же вписано и то, что делать с файлами,
    // которые браузер показать не умеет, — вместо пустоты человек получает
    // объяснение и кнопку «Скачать».
    //
    // Содержимое подставляется по-разному, в зависимости от вида файла:
    //   картинка — тегом img, с масштабом;
    //   PDF      — встроенным окном (iframe), его рисует сам браузер;
    //   текст    — забираем содержимое и выводим как текст, не как разметку;
    //   документ Office и архив — сервер присылает готовую разметку;
    //   видео и звук — тегами video и audio, проигрывает сам браузер.
    //
    // Отдаёт файлы обработчик Preview: он проверяет права заново и отдаёт
    // только то, что есть в белом списке (см. PreviewSupport.cs).
    // ======================================================================

    const preview = (function () {
        const modal = $('[data-modal="preview"]');

        if (!modal) {
            return { open() {}, close() {}, isOpen() { return false; } };
        }

        const body = $('[data-preview-body]', modal);
        const nameField = $('[data-preview-name]', modal);
        const extField = $('[data-preview-ext]', modal);
        const kindBox = $('[data-preview-kind]', modal);
        const downloadLink = $('[data-preview-download]', modal);
        const factsList = $('[data-preview-facts]', modal);
        const accessList = $('[data-preview-access]', modal);
        const zoomBar = $('[data-preview-zoom]', modal);
        const zoomValue = $('[data-zoom-value]', modal);

        let currentId = null;

        // Номер показа. Растёт при каждом открытии файла в панели.
        //
        // Одного currentId мало: один и тот же файл можно начать открывать
        // дважды подряд — так и происходит при двойном нажатии, когда сначала
        // срабатывает одиночное (панель уже открыта и следует за выделением),
        // а следом двойное. Оба запроса доходили до конца и дописывали
        // содержимое в панель, и документ показывался дважды.
        //
        // Теперь каждый показ получает свой номер, и дописать себя в панель
        // имеет право только последний.
        let requestNumber = 0;

        // Сколько ждём ответа, прежде чем признать, что связи нет.
        // На канале между офисами ответ может идти долго, поэтому
        // срок щедрый — но не бесконечный: висящее «Загрузка…»
        // хуже честного «не дождались».
        const WAIT_MS = 45000;

        // ---------- Масштаб картинки ----------
        //
        // Ступени подобраны так, чтобы каждое нажатие давало заметную
        // разницу. 100% здесь — это «вписать в окно», а не настоящий размер
        // в точках: снимок экрана шириной 3840 точек иначе открывался бы
        // кусочком своего угла.

        const ZOOM_STEPS = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4];
        const ZOOM_FIT = 3;
        let zoomIndex = ZOOM_FIT;

        function applyZoom() {
            const image = $('img', body);

            if (!image) {
                return;
            }

            const scale = ZOOM_STEPS[zoomIndex];

            image.style.maxWidth = scale <= 1 ? '100%' : 'none';
            image.style.width = scale <= 1 ? 'auto' : (scale * 100) + '%';

            zoomValue.textContent = Math.round(scale * 100) + '%';
        }

        function setZoomVisible(visible) {
            zoomBar.hidden = !visible;

            if (visible) {
                zoomIndex = ZOOM_FIT;
                zoomValue.textContent = '100%';
            }
        }

        $$('[data-zoom]', modal).forEach(button => {
            button.addEventListener('click', () => {
                zoomIndex = button.dataset.zoom === 'in'
                    ? Math.min(ZOOM_STEPS.length - 1, zoomIndex + 1)
                    : Math.max(0, zoomIndex - 1);

                applyZoom();
            });
        });

        function note(text, icon, extra) {
            body.innerHTML = '';

            const box = document.createElement('div');
            box.className = 'preview__note';

            if (icon) {
                const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                svg.setAttribute('class', 'icon icon--xl');
                const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
                use.setAttribute('href', '#' + icon);
                svg.appendChild(use);
                box.appendChild(svg);
            }

            const paragraph = document.createElement('p');
            paragraph.textContent = text;
            box.appendChild(paragraph);

            // Подробность мелким шрифтом: код ответа или причина.
            // Без неё разбираться с жалобой «не открывается» приходится
            // вслепую, а с ней сразу видно, дошёл ли запрос до сервера.
            if (extra) {
                const detail = document.createElement('p');
                detail.className = 'preview__detail';
                detail.textContent = extra;
                box.appendChild(detail);
            }

            body.appendChild(box);

            return box;
        }

        /**
         * Ошибка показа. Кроме объяснения даёт две кнопки: скачать
         * и открыть в отдельной вкладке — вторая часто срабатывает там,
         * где встроенное окно просмотра запрещено настройками браузера.
         */
        function failure(text, detail, file, reason) {
            // Истёкший вход — не поломка файла, и предлагать «скачать»
            // тут бессмысленно: скачивание тоже не сработает. Нужно другое —
            // сказать прямо, что произошло, и дать войти заново.
            if (reason === 401) {
                const box = note(
                    'Вход в портал истёк.',
                    'i-warning',
                    'страница была открыта слишком долго — войдите заново');

                const actions = document.createElement('div');
                actions.className = 'preview__actions';

                const reload = document.createElement('a');
                reload.className = 'btn btn--primary';
                reload.href = location.href;
                reload.textContent = 'Обновить страницу';

                actions.appendChild(reload);
                box.appendChild(actions);

                return;
            }

            const box = note(text, 'i-warning', detail);

            const actions = document.createElement('div');
            actions.className = 'preview__actions';

            const download = document.createElement('a');
            download.className = 'btn';
            download.href = file.downloadUrl;
            download.setAttribute('download', '');
            download.textContent = 'Скачать файл';

            const open = document.createElement('a');
            open.className = 'btn btn--quiet';
            open.href = '/Files?handler=Preview&fileId=' + file.id;
            open.target = '_blank';
            open.rel = 'noopener';
            open.textContent = 'Открыть в новой вкладке';

            actions.appendChild(download);
            actions.appendChild(open);
            box.appendChild(actions);
        }

        /**
         * Полоска ожидания. Показывается для ЛЮБОГО вида файла, в том числе
         * для картинок и PDF: раньше они просто вставлялись в панель, и пока
         * файл шёл по сети, человек видел пустое белое место и решал,
         * что портал сломался.
         */
        /** Собирает полоску ожидания, ничего не трогая на странице. */
        function loadingBox(name) {
            const box = document.createElement('div');
            box.className = 'preview__loading';

            const dial = document.createElement('div');
            dial.className = 'preview__spinner';

            const caption = document.createElement('p');
            caption.textContent = 'Загружается ' + (name || 'файл') + '…';

            const bar = document.createElement('div');
            bar.className = 'preview__bar';
            bar.appendChild(document.createElement('span'));

            box.appendChild(dial);
            box.appendChild(caption);
            box.appendChild(bar);

            return box;
        }

        function loading(name) {
            body.innerHTML = '';

            const box = loadingBox(name);

            body.appendChild(box);

            return box;
        }

        /**
         * Показать готовое содержимое: панель очищается полностью.
         *
         * Именно очищается, а не дополняется. Дописывание было ошибкой:
         * если до этого в панели что-то оставалось, документ показывался
         * дважды подряд.
         */
        function show(element) {
            body.innerHTML = '';
            body.appendChild(element);
        }

        /** Убирает полоску ожидания, не трогая остальное содержимое панели. */
        function doneLoading() {
            const box = $('.preview__loading', body);

            if (box) {
                box.remove();
            }
        }

        /**
         * Человеческое объяснение неудачного запроса.
         * Отдельной функцией, потому что причин ровно три, и путать их нельзя:
         * «сервер отказал», «сервера не слышно» и «ждали слишком долго».
         */
        function describeFailure(reason) {
            if (reason === 'timeout') {
                return 'ответ не пришёл за ' + Math.round(WAIT_MS / 1000) + ' с';
            }

            if (reason === 401) {
                return 'вход в портал истёк';
            }

            if (typeof reason === 'number') {
                if (reason === 403) {
                    return 'нет прав на этот файл (код 403)';
                }

                if (reason === 404) {
                    return 'сервер ответил «файл не найден» (код 404)';
                }

                return 'сервер ответил кодом ' + reason;
            }

            return 'связь с сервером прервалась';
        }

        /**
         * Запрос к порталу с ограничением по времени.
         *
         * credentials указан явно: в старых сборках браузеров fetch
         * по умолчанию НЕ отправлял cookie, и запрос уходил как от гостя —
         * сервер отвечал перенаправлением на вход, а человек видел
         * невнятную ошибку.
         */
        function request(url) {
            const controller = typeof AbortController === 'function' ? new AbortController() : null;
            let timedOut = false;

            const timer = setTimeout(() => {
                timedOut = true;

                if (controller) {
                    controller.abort();
                }
            }, WAIT_MS);

            const options = {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            };

            if (controller) {
                options.signal = controller.signal;
            }

            return fetch(url, options)
                .then(response => {
                    clearTimeout(timer);

                    return response.ok ? response : Promise.reject(response.status);
                })
                .catch(error => {
                    clearTimeout(timer);

                    return Promise.reject(timedOut ? 'timeout' : (typeof error === 'number' ? error : null));
                });
        }

        /**
         * Наполняет колонку «Сведения» и «Доступ».
         *
         * Данные берутся у того же обработчика, что и окно свойств:
         * заводить второй, отвечающий почти тем же самым, незачем.
         */
        function loadFacts(fileId) {
            factsList.innerHTML = '';
            accessList.innerHTML = '';

            request('/Files?handler=Properties&fileId=' + fileId)
                .then(response => response.json())
                .then(data => {
                    // Пока ходили за сведениями, могли открыть другой файл.
                    if (currentId !== fileId) {
                        return;
                    }

                    (data.rows || []).forEach(row => {
                        const name = document.createElement('dt');
                        name.textContent = row.name;

                        const value = document.createElement('dd');
                        value.textContent = row.value;

                        factsList.appendChild(name);
                        factsList.appendChild(value);
                    });

                    const groups = data.access || [];

                    if (groups.length === 0) {
                        const item = document.createElement('li');
                        item.className = 'muted';
                        item.textContent = 'Отдельных прав нет — действуют права родительской папки';
                        accessList.appendChild(item);
                        return;
                    }

                    groups.forEach(text => {
                        const item = document.createElement('li');
                        item.textContent = text;
                        accessList.appendChild(item);
                    });
                })
                .catch(() => {
                    // Сведения — дополнение, а не главное. Не получилось —
                    // окно всё равно показывает файл, ругаться незачем.
                });
        }

        return {
            isOpen() {
                return !modal.hidden;
            },

            currentId() {
                return currentId;
            },

            /**
             * file: { id, name, kind, family, size, downloadUrl }
             * kind — чем показывать: "image" | "pdf" | "text" | "office"
             *        | "video" | "audio" | "archive" | "" (показать нельзя)
             * family — каким цветом рисовать значок (см. FileKinds на сервере)
             */
            open(file, maxTextBytes) {
                currentId = file.id;

                const token = ++requestNumber;

                /** Этот показ ещё нужен, или его уже обогнал следующий? */
                const current = () => token === requestNumber;

                nameField.textContent = file.name;
                nameField.title = file.name;
                downloadLink.href = file.downloadUrl;

                // Значок в заголовке — тот же цветной прямоугольник, что
                // и в списке: окно должно узнаваться как «тот самый файл».
                const dot = file.name.lastIndexOf('.');
                const extension = dot > 0 ? file.name.slice(dot + 1).toUpperCase() : '';

                extField.textContent = extension.length > 4 ? '' : extension;
                kindBox.dataset.kind = file.family || 'other';

                setZoomVisible(file.kind === 'image');

                openModal('preview');
                loadFacts(file.id);

                const source = '/Files?handler=Preview&fileId=' + file.id;

                loading(file.name);

                if (file.kind === 'image') {
                    const image = document.createElement('img');
                    image.alt = file.name;

                    image.addEventListener('load', () => {
                        if (!current()) {
                            return;
                        }

                        show(image);
                        applyZoom();
                    });

                    image.addEventListener('error', () => {
                        if (current()) {
                            failure('Не удалось показать изображение.',
                                'файл не дошёл или повреждён', file);
                        }
                    });

                    image.src = source;
                    return;
                }

                if (file.kind === 'pdf') {
                    // PDF показывает сам браузер. Это удобно, но и уязвимо:
                    // встроенный просмотрщик можно отключить групповой
                    // политикой, и тогда окно остаётся пустым БЕЗ ошибки.
                    // Поэтому ждём события «загрузилось», а если его нет —
                    // предлагаем открыть файл отдельно.
                    const frame = document.createElement('iframe');
                    frame.title = file.name;
                    frame.hidden = true;

                    let shown = false;

                    const giveUp = setTimeout(() => {
                        if (!shown && current()) {
                            failure(
                                'Не удалось показать PDF во встроенном окне.',
                                'возможно, просмотр PDF отключён настройками браузера',
                                file);
                        }
                    }, WAIT_MS);

                    frame.addEventListener('load', () => {
                        shown = true;
                        clearTimeout(giveUp);

                        if (current()) {
                            doneLoading();
                            frame.hidden = false;
                        }
                    });

                    frame.addEventListener('error', () => {
                        clearTimeout(giveUp);

                        if (current()) {
                            failure('Не удалось показать PDF.', 'браузер отказался открыть файл', file);
                        }
                    });

                    // Адрес задаём ДО вставки в страницу.
                    //
                    // Пустой iframe, попав в разметку, сразу выдаёт событие
                    // «загрузилось» — про пустую страницу about:blank. Если
                    // вставить его первым, а адрес прописать вторым, наш
                    // обработчик срабатывает на это пустое событие: полоска
                    // ожидания исчезает, окно показывается — и человек видит
                    // белый прямоугольник вместо документа.
                    frame.src = source;
                    body.appendChild(frame);
                    return;
                }

                if (file.kind === 'office') {
                    // Сервер присылает не файл, а уже разобранное содержимое.
                    // Вставляем как разметку осознанно: составлял её портал,
                    // весь текст документа в ней закодирован (см. OfficeDocuments),
                    // а исполнить что-либо вставленное таким способом браузер
                    // не даст — код со страницы запрещён её политикой безопасности.
                    request('/Files?handler=OfficePreview&fileId=' + file.id)
                        .then(response => response.text())
                        .then(markup => {
                            if (!current()) {
                                return;
                            }

                            const holder = document.createElement('div');
                            holder.innerHTML = markup;

                            show(holder);
                        })
                        .catch(reason => {
                            if (current()) {
                                failure('Не удалось получить содержимое документа.',
                                    describeFailure(reason), file, reason);
                            }
                        });

                    return;
                }

                if (file.kind === 'text') {
                    if (maxTextBytes > 0 && file.size > maxTextBytes) {
                        note('Файл слишком большой для просмотра — откройте его после скачивания.',
                            'i-warning');
                        return;
                    }

                    request(source)
                        .then(response => response.text())
                        .then(text => {
                            // Только если за время загрузки не начали показывать другое.
                            if (!current()) {
                                return;
                            }

                            const block = document.createElement('pre');
                            // textContent, а не innerHTML: содержимое файла —
                            // это данные, и разметкой оно становиться не должно.
                            block.textContent = text;

                            show(block);
                        })
                        .catch(reason => {
                            if (current()) {
                                failure('Не удалось прочитать файл.', describeFailure(reason), file, reason);
                            }
                        });

                    return;
                }

                if (file.kind === 'video' || file.kind === 'audio') {
                    // Проигрывает сам браузер. Файл при этом качается кусками
                    // (сервер отдаёт его с поддержкой диапазонов), поэтому
                    // запись на сотни мегабайт начинает играть сразу,
                    // а не после полной загрузки — важно на канале между офисами.
                    const player = document.createElement(file.kind);
                    player.controls = true;
                    player.preload = 'metadata';
                    player.className = 'player player--' + file.kind;
                    player.hidden = true;

                    const box = loadingBox(file.name);

                    player.addEventListener('loadeddata', () => {
                        if (current()) {
                            box.remove();
                            player.hidden = false;
                        }
                    });

                    player.addEventListener('error', () => {
                        if (current()) {
                            failure(
                                'Не удалось проиграть запись.',
                                'браузер не понимает этот формат — скачайте файл',
                                file);
                        }
                    });

                    player.src = source;

                    // Показываем сразу и проигрыватель, и полоску ожидания:
                    // пока не пришли первые данные, человек должен видеть,
                    // что что-то происходит.
                    body.innerHTML = '';
                    body.appendChild(box);
                    body.appendChild(player);
                    return;
                }

                if (file.kind === 'archive') {
                    // Внутрь архива сервер не лезет: он читает только оглавление
                    // и присылает готовый список. Сами файлы не распаковываются.
                    request('/Files?handler=ArchivePreview&fileId=' + file.id)
                        .then(response => response.text())
                        .then(markup => {
                            if (!current()) {
                                return;
                            }

                            const holder = document.createElement('div');
                            holder.innerHTML = markup;

                            show(holder);
                        })
                        .catch(reason => {
                            if (current()) {
                                failure('Не удалось прочитать архив.',
                                    describeFailure(reason), file, reason);
                            }
                        });

                    return;
                }

                // Всё остальное — чертежи, базы, редкие форматы. Браузер такое
                // не показывает, а разобрать сами мы умеем только документы
                // Office и архивы ZIP (см. OfficeDocuments).
                note(
                    'Такой файл браузер показать не умеет. Нажмите «Скачать» вверху окна, ' +
                    'и файл откроется в своей программе.',
                    'i-file');
            },

            close() {
                currentId = null;

                closeModal(modal);

                setTimeout(() => {
                    // Содержимое убираем ПОСЛЕ движения: иначе окно исчезает
                    // пустым, и это заметно. Заодно останавливается
                    // проигрывание — иначе видео продолжало бы играть
                    // за закрытым окном.
                    if (modal.hidden) {
                        body.innerHTML = '';
                    }
                }, 250);
            }
        };
    })();

    // ----------------------------------------------------------------------
    // Кнопки в заголовке окна предпросмотра
    // ----------------------------------------------------------------------

    (function () {
        const modal = $('[data-modal="preview"]');

        if (!modal) {
            return;
        }

        // «Сведения»: узкую колонку справа можно убрать, чтобы отдать
        // всё место документу.
        const asideToggle = $('[data-preview-aside-toggle]', modal);

        if (asideToggle) {
            asideToggle.classList.add('is-active');

            asideToggle.addEventListener('click', () => {
                const hidden = modal.classList.toggle('pv--no-aside');

                asideToggle.classList.toggle('is-active', !hidden);
            });
        }

        // «Ссылка»: копирует адрес папки с этим файлом.
        //
        // Ссылка ОБЫЧНАЯ, не «публичная»: анонимного доступа портал не даёт
        // вовсе. Тот, кому вы её пошлёте, увидит папку, только если у него
        // и так есть к ней доступ. Это сделано намеренно — иначе ссылка
        // из переписки стала бы дырой в разграничении прав.
        const share = $('[data-preview-share]', modal);

        if (share) {
            share.addEventListener('click', () => {
                const link = location.origin + location.pathname + location.search;

                // navigator.clipboard работает только по защищённому
                // соединению. Портал пока живёт по HTTP, поэтому нужен
                // запасной путь через временное поле ввода.
                if (navigator.clipboard && window.isSecureContext) {
                    navigator.clipboard.writeText(link)
                        .then(() => toasts.show('Ссылка скопирована', 'info'))
                        .catch(() => copyThroughField(link));
                } else {
                    copyThroughField(link);
                }
            });
        }

        function copyThroughField(text) {
            const field = document.createElement('textarea');
            field.value = text;
            field.setAttribute('readonly', '');
            field.style.position = 'fixed';
            field.style.opacity = '0';

            document.body.appendChild(field);
            field.select();

            let copied = false;

            try {
                copied = document.execCommand('copy');
            } catch (error) {
                copied = false;
            }

            field.remove();

            toasts.show(
                copied
                    ? 'Ссылка скопирована'
                    : 'Не удалось скопировать — возьмите адрес из строки браузера',
                copied ? 'info' : 'error');
        }
    })();

    document.addEventListener('click', event => {
        // Закрытие окна предпросмотра идёт через preview.close(), а не через
        // общий обработчик окон: здесь нужно ещё остановить проигрывание
        // и забыть номер открытого файла.
        if (event.target.closest('[data-modal="preview"] [data-modal-close]')) {
            preview.close();
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && preview.isOpen()) {
            preview.close();
        }
    });

    // ======================================================================
    // 7. Файловый менеджер
    // ======================================================================

    const explorer = $('[data-explorer]');

    if (explorer) {
        initExplorer(explorer);
    }

    function initExplorer(root) {
        const folderId = root.dataset.folderId ? parseInt(root.dataset.folderId, 10) : null;
        const canWrite = root.dataset.canWrite === 'true';
        const canManage = root.dataset.canManage === 'true';
        const canCreateFolder = root.dataset.canCreateFolder === 'true';
        const maxSize = parseInt(root.dataset.maxSize, 10) || 0;
        const maxTextPreview = parseInt(root.dataset.maxTextPreview, 10) || 0;

        const pane = $('[data-drop-zone]', root);
        const veil = $('[data-drop-veil]', root);
        const rubber = $('[data-rubber]', root);
        const fileInput = $('#file-input');

        // Ширину полосы квоты задаём здесь, а не в разметке: встроенные
        // стили в HTML запрещены политикой безопасности страницы.
        $$('.quota__fill').forEach(fill => {
            fill.style.width = (fill.dataset.percent || 0) + '%';
        });

        wireUpload();
        wireRenameForm();

        // На странице результатов поиска списка папки нет — значит нет
        // ни выделения, ни перетаскивания, ни меню. Всё, что можно было
        // подключить (загрузка, создание папки), уже подключено выше.
        if (!pane) {
            return;
        }

        // ---------- Выделение ----------

        let selection = new Set();
        let lastClicked = null;

        // Признак «только что выделяли рамкой». Нужен потому, что после
        // протягивания браузер всё равно шлёт обычное нажатие по пустому
        // месту, а оно сбрасывает выделение — то самое, которое мы только
        // что и сделали.
        let rubberDragged = false;

        function tiles() {
            return $$('.tile', pane);
        }

        function fileTiles() {
            return $$('.tile--file', pane);
        }

        function selectedFileIds() {
            return Array.from(selection);
        }

        function repaintSelection() {
            fileTiles().forEach(tile => {
                tile.classList.toggle('is-selected', selection.has(parseInt(tile.dataset.file, 10)));
            });
        }

        function clearSelection() {
            selection.clear();
            repaintSelection();
        }

        function selectRange(fromTile, toTile) {
            const all = fileTiles();
            const from = all.indexOf(fromTile);
            const to = all.indexOf(toTile);

            if (from === -1 || to === -1) {
                return;
            }

            const [start, end] = from < to ? [from, to] : [to, from];

            for (let i = start; i <= end; i++) {
                selection.add(parseInt(all[i].dataset.file, 10));
            }
        }

        // Одиночное нажатие выделяет файл, а не открывает его: так же,
        // как в проводнике Windows. Открывается файл двойным нажатием.
        pane.addEventListener('click', event => {
            if (rubberDragged) {
                rubberDragged = false;
                return;
            }

            // Звёздочка «в избранном» лежит внутри плитки, а плитка — ссылка.
            // Перехватываем нажатие раньше всего остального, иначе вместе
            // с отметкой открылась бы сама папка или скачался файл.
            const star = event.target.closest('.fav-star');

            if (star) {
                event.preventDefault();
                event.stopPropagation();
                toggleFavorite(star);
                return;
            }

            const tile = event.target.closest('.tile--file');

            if (!tile) {
                if (!event.target.closest('.tile')) {
                    clearSelection();
                }
                return;
            }

            event.preventDefault();

            const id = parseInt(tile.dataset.file, 10);

            if (event.shiftKey && lastClicked) {
                selectRange(lastClicked, tile);
            } else if (event.ctrlKey || event.metaKey) {
                if (selection.has(id)) {
                    selection.delete(id);
                } else {
                    selection.add(id);
                }
                lastClicked = tile;
            } else {
                selection = new Set([id]);
                lastClicked = tile;

                // Если окно предпросмотра открыто, оно следует за выделением —
                // так же, как область просмотра в проводнике Windows.
                if (preview.isOpen() && tile.dataset.previewKind) {
                    showPreview(tile);
                }
            }

            repaintSelection();
        });

        // Двойное нажатие — открыть.
        //
        // Папка открывается переходом, файл — предпросмотром, если портал
        // умеет его показать. Всё остальное скачивается: показать такое
        // браузер не может, а значит «открыть» для него и есть «скачать».
        pane.addEventListener('dblclick', event => {
            const tile = event.target.closest('.tile');

            if (!tile) {
                return;
            }

            if (tile.classList.contains('tile--file') && tile.dataset.previewKind) {
                showPreview(tile);
                return;
            }

            window.location.href = tile.href;
        });

        // ---------- Выделение рамкой («резинка») ----------
        //
        // Нажали на пустом месте и потянули — рисуется прямоугольник,
        // и всё, чего он коснулся, выделяется. Как в проводнике.
        //
        // События pointer*, а не mouse*: они одинаково работают и мышью,
        // и пером, и пальцем, и нам не приходится писать три набора кода.
        // Захват указателя (setPointerCapture) нужен, чтобы рамка не «терялась»,
        // если курсор ушёл за пределы области.

        if (rubber) {
            let origin = null;

            pane.addEventListener('pointerdown', event => {
                // Только левая кнопка и только по пустому месту:
                // нажатие на плитке — это выделение или перетаскивание.
                if (event.button !== 0 || event.target.closest('.tile')) {
                    return;
                }

                const bounds = pane.getBoundingClientRect();

                origin = { x: event.clientX, y: event.clientY, box: bounds };

                if (!event.ctrlKey && !event.metaKey) {
                    clearSelection();
                }

                pane.setPointerCapture(event.pointerId);
            });

            pane.addEventListener('pointermove', event => {
                if (!origin) {
                    return;
                }

                const left = Math.min(origin.x, event.clientX);
                const top = Math.min(origin.y, event.clientY);
                const width = Math.abs(event.clientX - origin.x);
                const height = Math.abs(event.clientY - origin.y);

                // Пока не потянули хотя бы несколько точек, рамку не показываем:
                // иначе она мигает при обычном нажатии по пустому месту.
                if (width < 4 && height < 4) {
                    return;
                }

                rubber.hidden = false;
                rubberDragged = true;

                // Координаты у нас экранные, а рамка лежит внутри области —
                // поэтому вычитаем её положение и учитываем прокрутку.
                rubber.style.left = (left - origin.box.left + pane.scrollLeft) + 'px';
                rubber.style.top = (top - origin.box.top + pane.scrollTop) + 'px';
                rubber.style.width = width + 'px';
                rubber.style.height = height + 'px';

                const frame = { left: left, top: top, right: left + width, bottom: top + height };

                fileTiles().forEach(tile => {
                    const box = tile.getBoundingClientRect();

                    const touches = box.left < frame.right && box.right > frame.left
                        && box.top < frame.bottom && box.bottom > frame.top;

                    if (touches) {
                        selection.add(parseInt(tile.dataset.file, 10));
                    }
                });

                repaintSelection();

                // Без этого браузер начинает выделять текст на странице.
                event.preventDefault();
            });

            ['pointerup', 'pointercancel'].forEach(name => {
                pane.addEventListener(name, event => {
                    if (!origin) {
                        return;
                    }

                    origin = null;
                    rubber.hidden = true;

                    if (pane.hasPointerCapture(event.pointerId)) {
                        pane.releasePointerCapture(event.pointerId);
                    }
                });
            });
        }

        // ---------- Контекстное меню ----------

        pane.addEventListener('contextmenu', event => {
            const fileTile = event.target.closest('.tile--file');
            const folderTile = event.target.closest('.tile--folder');

            event.preventDefault();

            if (fileTile) {
                const id = parseInt(fileTile.dataset.file, 10);

                // Нажатие правой кнопкой по невыделенному файлу сначала выделяет его.
                if (!selection.has(id)) {
                    selection = new Set([id]);
                    lastClicked = fileTile;
                    repaintSelection();
                }

                showMenu(event.clientX, event.clientY, fileMenuItems(fileTile));
                return;
            }

            if (folderTile) {
                showMenu(event.clientX, event.clientY, folderMenuItems(folderTile));
                return;
            }

            clearSelection();
            showMenu(event.clientX, event.clientY, emptySpaceMenuItems());
        });

        function fileMenuItems(tile) {
            const ids = selectedFileIds();
            const canDelete = tile.dataset.canDelete === 'true';
            const one = ids.length === 1;
            const items = [];

            if (one && tile.dataset.previewKind) {
                items.push({
                    label: 'Просмотр', icon: 'i-eye',
                    onClick: () => showPreview(tile)
                });
            }

            if (one) {
                items.push({
                    label: 'Скачать', icon: 'i-download',
                    onClick: () => { window.location.href = tile.href; }
                });

                items.push({ separator: true });

                if (canDelete) {
                    items.push({
                        label: 'Переименовать', icon: 'i-rename',
                        onClick: () => openRename('file', parseInt(tile.dataset.file, 10), tile.dataset.name)
                    });
                }

                items.push({
                    label: 'Свойства', icon: 'i-info',
                    onClick: () => openProperties('fileId=' + tile.dataset.file)
                });
            }

            if (canDelete) {
                items.push({ separator: true });

                items.push({
                    label: 'Удалить' + countSuffix(ids.length), icon: 'i-trash', danger: true,
                    onClick: () => deleteSelected()
                });
            }

            return items;
        }

        /**
         * Меню для папки.
         *
         * Права берём из атрибута САМОЙ плитки, а не из общего значения
         * для страницы. Именно из-за этого раньше не удалялись папки верхнего
         * уровня: на верхнем уровне «текущей папки» нет, права страницы там
         * равны нулю — и пункты управления не появлялись даже у администратора.
         */
        function folderMenuItems(tile) {
            const id = parseInt(tile.dataset.folder, 10);
            const canManageThis = tile.dataset.canManage === 'true';

            const items = [
                {
                    label: 'Открыть', icon: 'i-open',
                    onClick: () => { window.location.href = tile.href; }
                }
            ];

            if (canManageThis) {
                items.push({
                    label: 'Переименовать', icon: 'i-rename',
                    onClick: () => openRename('folder', id, tile.dataset.name)
                });

                items.push({
                    label: 'Управление папкой', icon: 'i-settings',
                    onClick: () => { window.location.href = '/Files/Settings/' + id; }
                });
            }

            items.push({
                label: 'Свойства', icon: 'i-info',
                onClick: () => openProperties('folderId=' + id)
            });

            if (canManageThis) {
                items.push({ separator: true });

                items.push({
                    label: 'Удалить папку', icon: 'i-trash', danger: true,
                    onClick: () => {
                        confirmDialog(
                            'Удалить папку «' + tile.dataset.name + '»? ' +
                            'Удалить можно только пустую папку — без подпапок и файлов, включая корзину.',
                            'Удалить папку'
                        ).then(ok => {
                            if (ok) {
                                submitForm($('[data-form="delete-folder"]'), { folderId: id });
                            }
                        });
                    }
                });
            }

            return items;
        }

        function emptySpaceMenuItems() {
            const items = [];

            if (canWrite) {
                items.push({
                    label: 'Загрузить файлы', icon: 'i-upload',
                    onClick: () => fileInput && fileInput.click()
                });
            }

            if (canCreateFolder) {
                items.push({
                    label: 'Создать папку', icon: 'i-new-folder',
                    onClick: () => openModal('new-folder')
                });
            }

            // Пункты про «эту папку» имеют смысл, только когда мы внутри
            // папки. На верхнем уровне текущей папки нет — и предлагать
            // её свойства или управление ею нечего.
            if (folderId !== null) {
                items.push({ separator: true });

                items.push({
                    label: 'Свойства папки', icon: 'i-info',
                    onClick: () => openProperties('folderId=' + folderId)
                });

                if (canManage) {
                    items.push({
                        label: 'Управление папкой', icon: 'i-settings',
                        onClick: () => { window.location.href = '/Files/Settings/' + folderId; }
                    });
                }
            }

            return items;
        }

        /**
         * Поставить или снять звёздочку.
         *
         * Страница при этом не перезагружается: сервер отвечает новым
         * состоянием, а звёздочка перекрашивается на месте. Человек
         * не теряет ни выделение, ни прокрутку — а ради одной отметки
         * перерисовывать всю страницу было бы расточительно.
         */
        function toggleFavorite(star) {
            const fileId = star.dataset.favoriteFile;
            const folderId = star.dataset.favoriteFolder;

            const address = '/Files?handler=Favorite'
                + (fileId ? '&fileId=' + fileId : '&folderId=' + folderId);

            const body = new FormData();
            body.append('__RequestVerificationToken', antiforgeryToken());

            fetch(address, {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                body
            })
                .then(response => (response.ok ? response.json() : Promise.reject(response.status)))
                .then(data => star.classList.toggle('is-on', data.favorite))
                .catch(() => toasts.show('Не удалось изменить избранное', 'error'));
        }

        /** Открыть файл в окне предпросмотра. */
        function showPreview(tile) {
            preview.open({
                id: parseInt(tile.dataset.file, 10),
                name: tile.dataset.name,

                // kind — чем показывать (картинка, PDF, текст…),
                // family — каким цветом рисовать значок. Это разные вопросы:
                // показать умеем не всё, а покрасить — всё.
                kind: tile.dataset.previewKind || '',
                family: tile.dataset.kind || 'other',
                size: parseInt(tile.dataset.size, 10) || 0,
                downloadUrl: tile.getAttribute('href')
            }, maxTextPreview);
        }

        function countSuffix(count) {
            return count > 1 ? ' (' + count + ')' : '';
        }

        function deleteSelected() {
            const ids = selectedFileIds();

            if (ids.length === 0) {
                return;
            }

            const question = ids.length === 1
                ? 'Переместить файл в корзину? Восстановить его можно будет из раздела «Корзина».'
                : 'Переместить в корзину файлов: ' + ids.length +
                  '? Восстановить их можно будет из раздела «Корзина».';

            confirmDialog(question, 'В корзину').then(ok => {
                if (ok) {
                    submitForm($('[data-form="delete-files"]'), { fileIds: ids });
                }
            });
        }

        // ---------- Клавиатура ----------

        document.addEventListener('keydown', event => {
            // Не мешаем печатать в полях ввода.
            const tag = (event.target.tagName || '').toLowerCase();

            if (tag === 'input' || tag === 'textarea' || tag === 'select') {
                return;
            }

            if ($('[data-modal].is-open')) {
                return;
            }

            const ctrl = event.ctrlKey || event.metaKey;

            if (ctrl && event.key.toLowerCase() === 'a') {
                event.preventDefault();
                selection = new Set(fileTiles().map(tile => parseInt(tile.dataset.file, 10)));
                repaintSelection();
                return;
            }

            // Ctrl+C, Ctrl+X и Ctrl+V внутри портала намеренно НЕ перехватываются.
            //
            // Раньше здесь был свой буфер обмена: «скопировать» в одной папке
            // и «вставить» в другой. Выглядело как проводник Windows, но им
            // не было — настоящий буфер обмена оставался нетронутым, и человек,
            // нажавший Ctrl+C на портале, а Ctrl+V у себя на рабочем столе,
            // не получал ничего. Путаницы больше, чем пользы.
            //
            // Теперь единственный смысл Ctrl+V — вставить файл С КОМПЬЮТЕРА
            // в папку портала, то есть загрузить его. Это обрабатывается
            // событием paste ниже. Обратного направления нет и быть не может:
            // положить файл в буфер обмена Windows браузер странице не даёт.
            // Файлы между папками портала переносятся перетаскиванием.

            if (event.key === 'Delete' && selection.size > 0) {
                event.preventDefault();
                deleteSelected();
                return;
            }

            // F2 — переименовать, как в проводнике.
            if (event.key === 'F2' && selection.size === 1) {
                const tile = fileTiles().find(
                    item => parseInt(item.dataset.file, 10) === selectedFileIds()[0]);

                if (tile && tile.dataset.canDelete === 'true') {
                    event.preventDefault();
                    openRename('file', parseInt(tile.dataset.file, 10), tile.dataset.name);
                }

                return;
            }

            // Пробел — показать выделенный файл в панели справа.
            if (event.key === ' ' && selection.size === 1) {
                const tile = fileTiles().find(
                    item => parseInt(item.dataset.file, 10) === selectedFileIds()[0]);

                if (tile && tile.dataset.previewKind) {
                    event.preventDefault();

                    if (preview.isOpen() && preview.currentId() === parseInt(tile.dataset.file, 10)) {
                        preview.close();
                    } else {
                        showPreview(tile);
                    }
                }
            }
        });

        // ---------- Вставка файлов из проводника (Ctrl+V) ----------

        document.addEventListener('paste', event => {
            if (!canWrite || !event.clipboardData) {
                return;
            }

            const files = Array.from(event.clipboardData.files || []);

            if (files.length > 0) {
                event.preventDefault();
                upload(files);
            }
        });

        // ---------- Перетаскивание ----------

        if (canWrite) {
            let dragDepth = 0;

            // dragenter/dragleave срабатывают и на вложенных элементах,
            // поэтому считаем «глубину»: подсказку убираем, только когда
            // указатель действительно покинул область.
            pane.addEventListener('dragenter', event => {
                if (!hasFiles(event)) {
                    return;
                }
                event.preventDefault();
                dragDepth++;
                veil.hidden = false;
                requestAnimationFrame(() => veil.classList.add('is-visible'));
            });

            pane.addEventListener('dragover', event => {
                if (hasFiles(event)) {
                    event.preventDefault();
                    event.dataTransfer.dropEffect = 'copy';
                }
            });

            pane.addEventListener('dragleave', () => {
                dragDepth = Math.max(0, dragDepth - 1);
                if (dragDepth === 0) {
                    hideVeil();
                }
            });

            pane.addEventListener('drop', event => {
                if (!hasFiles(event)) {
                    return;
                }

                event.preventDefault();
                dragDepth = 0;
                hideVeil();

                upload(Array.from(event.dataTransfer.files || []));
            });

            // Браузер по умолчанию открывает файл, брошенный мимо области.
            // Это выкидывает человека со страницы — перехватываем.
            ['dragover', 'drop'].forEach(name => {
                document.addEventListener(name, event => {
                    if (!event.target.closest('[data-drop-zone]') && hasFiles(event)) {
                        event.preventDefault();
                    }
                });
            });
        }

        function hasFiles(event) {
            return event.dataTransfer && Array.from(event.dataTransfer.types || []).indexOf('Files') !== -1;
        }

        function hideVeil() {
            veil.classList.remove('is-visible');
            setTimeout(() => { veil.hidden = true; }, 200);
        }

        // ---------- Перетаскивание файлов ----------
        //
        // Перетаскивание работает только ВНУТРИ портала: файл на папку —
        // это перемещение. Наружу, в проводник Windows, портал файлы
        // не отдаёт — единственный способ забрать файл на компьютер
        // это «Скачать».

        if (canWrite) {
            fileTiles().forEach(tile => {
                tile.draggable = true;

                tile.addEventListener('dragstart', event => {
                    const id = parseInt(tile.dataset.file, 10);

                    if (!selection.has(id)) {
                        selection = new Set([id]);
                        repaintSelection();
                    }

                    event.dataTransfer.setData(
                        'application/x-portal-files', JSON.stringify(selectedFileIds()));

                    event.dataTransfer.effectAllowed = 'move';
                    tile.classList.add('is-dragging');
                });

                tile.addEventListener('dragend', () => tile.classList.remove('is-dragging'));
            });
        }

        if (canWrite) {
            $$('[data-drop-target]', pane).forEach(target => {
                target.addEventListener('dragover', event => {
                    if (Array.from(event.dataTransfer.types).indexOf('application/x-portal-files') !== -1) {
                        event.preventDefault();
                        event.dataTransfer.dropEffect = 'move';
                        target.classList.add('is-drop-target');
                    }
                });

                target.addEventListener('dragleave', () => target.classList.remove('is-drop-target'));

                target.addEventListener('drop', event => {
                    const raw = event.dataTransfer.getData('application/x-portal-files');

                    if (!raw) {
                        return;
                    }

                    event.preventDefault();
                    event.stopPropagation();
                    target.classList.remove('is-drop-target');

                    submitForm($('[data-form="move"]'), {
                        targetFolderId: parseInt(target.dataset.dropTarget, 10),
                        fileIds: JSON.parse(raw)
                    });
                });
            });
        }

        // ---------- Загрузка с показом хода выполнения ----------

        function wireUpload() {
            if (fileInput) {
                $$('[data-action="pick-files"]').forEach(button => {
                    button.addEventListener('click', () => fileInput.click());
                });

                fileInput.addEventListener('change', () => {
                    const files = Array.from(fileInput.files || []);

                    if (files.length > 0) {
                        upload(files);
                    }

                    // Сбрасываем, иначе повторный выбор того же файла
                    // не вызовет события change.
                    fileInput.value = '';
                });
            }

            $$('[data-action="new-folder"]').forEach(button => {
                button.addEventListener('click', () => openModal('new-folder'));
            });
        }

        // ---------- Переименование ----------
        //
        // Окно одно на файлы и папки. Куда отправлять форму, известно только
        // в момент нажатия, поэтому адрес берём у соответствующей скрытой
        // формы — её сформировал сервер, и она переживёт смену маршрутов.

        function wireRenameForm() {
            const form = $('[data-rename-form]');

            if (!form) {
                return;
            }

            form.addEventListener('submit', event => {
                // Пустое имя сервер отвергнет, но лучше не доводить до запроса.
                const field = $('input[name="newName"]', form);

                if (!field.value.trim()) {
                    event.preventDefault();
                    field.focus();
                }
            });
        }

        function openRename(kind, id, currentName) {
            const form = $('[data-rename-form]');
            const source = $('[data-form="rename-' + kind + '"]');

            if (!form || !source) {
                return;
            }

            form.action = source.action;

            $('[data-rename-title]').textContent =
                kind === 'folder' ? 'Переименовать папку' : 'Переименовать файл';

            // Включаем только нужное поле: отключённые поля не отправляются,
            // и сервер получит ровно один номер, а не оба сразу.
            const fileField = $('[data-rename-file-id]', form);
            const folderField = $('[data-rename-folder-id]', form);

            fileField.disabled = kind !== 'file';
            folderField.disabled = kind !== 'folder';
            fileField.value = id;
            folderField.value = id;

            const field = $('input[name="newName"]', form);
            field.value = currentName;

            openModal('rename');

            // Выделяем имя без расширения — как в проводнике: меняют обычно
            // имя, а точку с расширением задевать не хотят. openModal уже
            // выделил всё, здесь сужаем выделение.
            const dot = currentName.lastIndexOf('.');

            if (kind === 'file' && dot > 0) {
                field.setSelectionRange(0, dot);
            }
        }

        // ---------- Свойства ----------

        function openProperties(query) {
            const modal = $('[data-modal="properties"]');

            if (!modal) {
                return;
            }

            const rows = $('[data-properties-rows]', modal);

            rows.innerHTML = '';
            $('[data-properties-title]', modal).textContent = 'Свойства';

            openModal('properties');

            fetch('/Files?handler=Properties&' + query, {
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => (response.ok ? response.json() : Promise.reject(response.status)))
                .then(data => {
                    $('[data-properties-title]', modal).textContent = data.title;

                    data.rows.forEach(row => {
                        const name = document.createElement('dt');
                        name.textContent = row.name;

                        const value = document.createElement('dd');
                        value.textContent = row.value;

                        rows.appendChild(name);
                        rows.appendChild(value);
                    });
                })
                .catch(() => {
                    const value = document.createElement('dd');
                    value.textContent = 'Не удалось получить сведения.';
                    rows.appendChild(value);
                });
        }

        function upload(files) {
            if (folderId === null) {
                toasts.show('Файлы можно загружать только внутрь папки.', 'error');
                return;
            }

            // Отсекаем заведомо слишком большое ещё до отправки: незачем
            // гонять сотню мегабайт по сети, чтобы получить отказ.
            const tooBig = files.filter(file => maxSize > 0 && file.size > maxSize);

            if (tooBig.length > 0) {
                toasts.show(
                    'Слишком большие файлы (предел ' + formatSize(maxSize) + '): ' +
                    tooBig.map(file => file.name).join(', '),
                    'error');
            }

            const allowed = files.filter(file => !(maxSize > 0 && file.size > maxSize));

            if (allowed.length === 0) {
                return;
            }

            const form = new FormData();
            form.append('__RequestVerificationToken', antiforgeryToken());
            allowed.forEach(file => form.append('uploads', file, file.name));

            const panel = showUploadPanel(allowed);
            const request = new XMLHttpRequest();

            request.open('POST', '/Files?handler=Upload&folderId=' + folderId);
            request.setRequestHeader('X-Requested-With', 'XMLHttpRequest');

            request.upload.addEventListener('progress', event => {
                if (event.lengthComputable) {
                    panel.setProgress(event.loaded / event.total);
                }
            });

            request.addEventListener('load', () => {
                panel.done();

                if (request.status !== 200) {
                    toasts.show('Загрузка не удалась (код ' + request.status + ').', 'error');
                    return;
                }

                let result = null;

                try {
                    result = JSON.parse(request.responseText);
                } catch (error) {
                    // Сервер ответил не тем, чего мы ждали, — просто обновим страницу.
                }

                if (result && result.rejected && result.rejected.length > 0) {
                    toasts.show(
                        'Не загружены: ' +
                        result.rejected.map(r => '«' + r.file + '» — ' + r.reason).join('; '),
                        'error');

                    // Даём прочитать сообщение, прежде чем страница обновится.
                    setTimeout(() => location.reload(), 6000);
                    return;
                }

                location.reload();
            });

            request.addEventListener('error', () => {
                panel.done();
                toasts.show('Загрузка прервана: нет связи с сервером.', 'error');
            });

            request.send(form);
        }

        function showUploadPanel(files) {
            const panel = document.createElement('div');
            panel.className = 'uploader';

            const total = files.reduce((sum, file) => sum + file.size, 0);

            panel.innerHTML =
                '<div class="uploader__title">Загрузка ' + files.length + ' файл(ов) · ' +
                formatSize(total) + '</div>' +
                '<div class="uploader__bar"><span class="uploader__fill"></span></div>' +
                '<div class="uploader__percent">0%</div>';

            document.body.appendChild(panel);
            requestAnimationFrame(() => panel.classList.add('is-visible'));

            const fill = $('.uploader__fill', panel);
            const percent = $('.uploader__percent', panel);

            return {
                setProgress(ratio) {
                    const value = Math.round(ratio * 100);
                    fill.style.width = value + '%';
                    percent.textContent = value + '%';
                },
                done() {
                    fill.style.width = '100%';
                    percent.textContent = 'готово';
                    setTimeout(() => {
                        panel.classList.remove('is-visible');
                        setTimeout(() => panel.remove(), 300);
                    }, 400);
                }
            };
        }
    }

    // ======================================================================
    // 10. Переписки
    //
    // Страница переписок обычная, серверная: отправка сообщения — это отправка
    // формы, переход в беседу — переход по ссылке. Здесь только то, без чего
    // пользоваться неудобно:
    //   • Enter отправляет, Shift+Enter переносит строку;
    //   • поле ввода растёт под длинный текст;
    //   • список сообщений прокручивается вниз при открытии;
    //   • раз в несколько секунд проверяется, не написал ли собеседник;
    //   • поиск людей для окон «Написать» и «Создать группу».
    //
    // Постоянного соединения нет и здесь — по той же причине, что и у
    // колокольчика: канал между офисами с урезанным MTU рвёт долгие
    // соединения, и портал выглядел бы зависшим.
    // ======================================================================

    (function () {
        const chat = $('[data-chat]');

        if (!chat) {
            return;
        }

        const conversationId = chat.dataset.conversation ? parseInt(chat.dataset.conversation, 10) : null;
        const lastMessageId = parseInt(chat.dataset.lastMessage, 10) || 0;

        // ---------- Прокрутка к последнему сообщению ----------

        const messages = $('[data-messages]', chat);

        if (messages) {
            messages.scrollTop = messages.scrollHeight;
        }

        // ---------- Поле ввода ----------

        const composer = $('[data-composer]');

        if (composer) {
            const text = $('[data-composer-text]', composer);
            const files = $('[data-composer-files]', composer);
            const chosenFiles = $('[data-composer-list]', composer);

            // Растим поле под текст, но не бесконечно: предел задан в стилях.
            function grow() {
                text.style.height = 'auto';
                text.style.height = Math.min(text.scrollHeight, 180) + 'px';
            }

            text.addEventListener('input', grow);
            grow();

            text.addEventListener('keydown', event => {
                if (event.key === 'Enter' && !event.shiftKey) {
                    event.preventDefault();

                    if (text.value.trim() || (files.files && files.files.length > 0)) {
                        composer.requestSubmit();
                    }
                }
            });

            $$('[data-action="attach"]').forEach(button => {
                button.addEventListener('click', () => files.click());
            });

            files.addEventListener('change', () => {
                const names = Array.from(files.files || []).map(f => f.name + ' · ' + formatSize(f.size));

                chosenFiles.textContent = names.length > 0 ? 'Приложено: ' + names.join(', ') : '';
                chosenFiles.hidden = names.length === 0;
            });

            // Файл, брошенный на окно переписки, прикладывается к сообщению.
            ['dragover', 'drop'].forEach(name => {
                composer.addEventListener(name, event => {
                    if (!event.dataTransfer || Array.from(event.dataTransfer.types).indexOf('Files') === -1) {
                        return;
                    }

                    event.preventDefault();

                    if (name === 'drop') {
                        files.files = event.dataTransfer.files;
                        files.dispatchEvent(new Event('change'));
                    }
                });
            });
        }

        // ---------- Проверка новых сообщений ----------

        if (conversationId !== null) {
            setInterval(() => {
                if (document.visibilityState !== 'visible') {
                    return;
                }

                fetch('/Messages?handler=New&id=' + conversationId + '&afterId=' + lastMessageId,
                    { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                    .then(response => (response.ok ? response.json() : Promise.reject(response.status)))
                    .then(data => {
                        if (data.hasNew) {
                            // Перезагружаем страницу целиком, а не дорисовываем
                            // сообщения по одному: так на экране гарантированно
                            // то же, что в базе, и кода в разы меньше.
                            location.reload();
                        }
                    })
                    .catch(() => { /* связь моргнула — попробуем в следующий раз */ });
            }, 7000);
        }

        // ---------- Удаление сообщения ----------

        document.addEventListener('click', event => {
            const button = event.target.closest('[data-delete-message]');

            if (!button) {
                return;
            }

            confirmDialog(
                'Удалить сообщение? Оно пропадёт у всех участников, на его месте ' +
                'останется пометка «сообщение удалено». Вложения будут стёрты с диска.',
                'Удалить'
            ).then(ok => {
                if (ok) {
                    submitForm($('[data-form="delete-message"]'), {
                        messageId: button.dataset.deleteMessage
                    });
                }
            });
        });

        // ---------- Управление группой ----------

        $$('[data-action="group-settings"]').forEach(button => {
            button.addEventListener('click', () => openPeopleModal('group-settings'));
        });

        document.addEventListener('click', event => {
            const remove = event.target.closest('[data-remove-member]');

            if (remove) {
                submitForm($('[data-form="remove-member"]'), {
                    memberUserName: remove.dataset.removeMember
                });
                return;
            }

            const leave = event.target.closest('[data-leave-group]');

            if (leave) {
                confirmDialog(
                    'Выйти из группы? Новые сообщения приходить перестанут, ' +
                    'а чтобы вернуться, придётся просить создателя добавить вас снова.',
                    'Выйти'
                ).then(ok => {
                    if (ok) {
                        submitForm($('[data-form="remove-member"]'), {
                            memberUserName: leave.dataset.leaveGroup
                        });
                    }
                });
            }
        });

        // ---------- Поиск сотрудников ----------
        //
        // Один и тот же список используют три окна: «Написать», «Создать
        // группу» и «Участники». Отличается только то, что делать по нажатию
        // на человека, — это и задаётся атрибутом на списке.

        // Готовые поиски по окнам: окно → функция «покажи список».
        // Нужно потому, что список должен наполниться СРАЗУ при открытии окна,
        // а не после того, как человек догадается щёлкнуть в поле поиска.
        const searches = new Map();

        $$('[data-people-search]').forEach(field => {
            const modal = field.closest('[data-modal]');
            const list = $('[data-people-list]', modal);

            if (!list) {
                return;
            }

            const multi = list.hasAttribute('data-multi');
            const addMember = list.hasAttribute('data-add-member');
            const chosen = $('[data-chosen]', modal);
            const picked = new Map();

            let timer = null;

            function repaintChosen() {
                if (!chosen) {
                    return;
                }

                chosen.innerHTML = '';

                picked.forEach((name, login) => {
                    const chip = document.createElement('span');
                    chip.className = 'chip';
                    chip.textContent = name;
                    chosen.appendChild(chip);

                    // Выбранные уходят на сервер скрытыми полями формы.
                    const input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = 'Members';
                    input.value = login;
                    chosen.appendChild(input);
                });
            }

            function render(people) {
                list.innerHTML = '';

                if (people.length === 0) {
                    const empty = document.createElement('div');
                    empty.className = 'people__empty';
                    empty.textContent = 'Никого не нашлось';
                    list.appendChild(empty);
                    return;
                }

                people.forEach(person => {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'people__item' + (picked.has(person.userName) ? ' is-chosen' : '');

                    const name = document.createElement('span');
                    name.textContent = person.displayName;

                    const login = document.createElement('span');
                    login.className = 'people__login';
                    login.textContent = person.userName;

                    button.appendChild(name);
                    button.appendChild(login);

                    button.addEventListener('click', () => {
                        if (multi) {
                            if (picked.has(person.userName)) {
                                picked.delete(person.userName);
                                button.classList.remove('is-chosen');
                            } else {
                                picked.set(person.userName, person.displayName);
                                button.classList.add('is-chosen');
                            }

                            repaintChosen();
                            return;
                        }

                        submitForm(
                            $('[data-form="' + (addMember ? 'add-member' : 'start') + '"]'),
                            addMember
                                ? { memberUserName: person.userName }
                                : { withUserName: person.userName });
                    });

                    list.appendChild(button);
                });
            }

            function search() {
                fetch('/Messages?handler=People&q=' + encodeURIComponent(field.value),
                    { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                    .then(response => (response.ok ? response.json() : Promise.reject(response.status)))
                    .then(render)
                    .catch(() => {
                        list.innerHTML = '';

                        const error = document.createElement('div');
                        error.className = 'people__empty';
                        error.textContent = 'Не удалось получить список сотрудников.';
                        list.appendChild(error);
                    });
            }

            // Ждём, пока человек допечатает: запрос на каждую букву
            // означал бы обращение к контроллеру домена на каждое нажатие.
            field.addEventListener('input', () => {
                clearTimeout(timer);
                timer = setTimeout(search, 250);
            });

            field.addEventListener('focus', () => {
                if (list.children.length === 0) {
                    search();
                }
            });

            if (modal) {
                searches.set(modal.dataset.modal, search);
            }
        });

        /** Открыть окно и сразу наполнить в нём список сотрудников. */
        function openPeopleModal(name) {
            openModal(name);

            const search = searches.get(name);

            if (search) {
                search();
            }
        }

        $$('[data-action="new-direct"]').forEach(button => {
            button.addEventListener('click', () => openPeopleModal('new-direct'));
        });

        $$('[data-action="new-group"]').forEach(button => {
            button.addEventListener('click', () => openPeopleModal('new-group'));
        });
    })();

    // ======================================================================
    // 11. Сворачивание длинных объявлений
    //
    // Лента из полотен текста нечитаема, поэтому длинные объявления
    // показываются началом, а по нажатию раскрываются целиком.
    //
    // Порог решается ПО ФАКТУ, а не по числу символов: одно и то же
    // количество текста занимает разную высоту на широком и узком экране.
    // Меряем настоящую высоту и сворачиваем только то, что действительно
    // не поместилось.
    //
    // Важно: класс, обрезающий текст, ставится отсюда. Если код страницы
    // почему-либо не отработает, объявление останется видно целиком —
    // это правильнее, чем спрятать его совсем.
    // ======================================================================

    (function () {
        const blocks = $$('[data-collapsible]');

        if (blocks.length === 0) {
            return;
        }

        /** Ниже этой высоты сворачивать нечего. Должно совпадать с max-height в стилях. */
        const LIMIT = 220;

        blocks.forEach(block => {
            if (block.scrollHeight <= LIMIT + 40) {
                return;
            }

            block.classList.add('is-collapsed');

            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'post__more';

            const label = document.createTextNode('Читать полностью');

            const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.setAttribute('class', 'icon');
            const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
            use.setAttribute('href', '#i-chevron-down');
            svg.appendChild(use);

            button.appendChild(label);
            button.appendChild(svg);

            button.addEventListener('click', () => {
                const collapsed = block.classList.toggle('is-collapsed');

                button.classList.toggle('is-open', !collapsed);
                label.textContent = collapsed ? 'Читать полностью' : 'Свернуть';

                // Свернули длинное объявление — возвращаем человека к его началу,
                // иначе он окажется где-то посреди следующего.
                if (collapsed) {
                    const top = block.getBoundingClientRect().top;

                    if (top < 0) {
                        block.scrollIntoView({ block: 'start', behavior: 'smooth' });
                    }
                }
            });

            block.insertAdjacentElement('afterend', button);
        });
    })();

    // ======================================================================
    // 8. Переключатель светлой и тёмной темы
    //
    // Сам выбор темы делает theme.js, подключённый в самом верху страницы.
    // Здесь только кнопка: показать текущий режим и переключить на следующий.
    // ======================================================================

    (function () {
        const button = $('[data-theme-toggle]');

        if (!button || !window.portalTheme) {
            return;
        }

        const icons = {
            auto: { icon: '#i-theme-auto', title: 'Тема: как в системе' },
            light: { icon: '#i-theme-light', title: 'Тема: светлая' },
            dark: { icon: '#i-theme-dark', title: 'Тема: тёмная' }
        };

        function repaint(mode) {
            const view = icons[mode] || icons.auto;

            $('[data-theme-icon]', button).setAttribute('href', view.icon);
            button.title = view.title + ' (нажмите, чтобы сменить)';
            button.setAttribute('aria-label', view.title);
        }

        repaint(window.portalTheme.current());

        button.addEventListener('click', () => repaint(window.portalTheme.cycle()));
    })();

    // ======================================================================
    // 8а. Вид списка файлов и порядок сортировки
    //
    // Вид (плитками или списком) — дело вкуса конкретного человека, сервер
    // про него не знает: выбор хранится в браузере и действует на всех
    // страницах портала. Порядок сортировки, наоборот, живёт в адресе
    // страницы — его можно послать ссылкой, и он переживает перезагрузку.
    // ======================================================================

    (function () {
        const grid = $('[data-file-grid]');
        const buttons = $$('[data-view]');

        // Список выбора «по имени / по дате…» отправляет форму сам,
        // без кнопки. Без JavaScript кнопка появится (см. noscript в разметке).
        $$('[data-autosubmit]').forEach(select => {
            select.addEventListener('change', () => select.form.submit());
        });

        if (buttons.length === 0) {
            return;
        }

        const STORAGE_KEY = 'portal.fileView';

        function apply(mode) {
            if (grid) {
                grid.classList.toggle('grid--list', mode === 'list');
            }

            buttons.forEach(button =>
                button.classList.toggle('is-active', button.dataset.view === mode));
        }

        // localStorage недоступен, если браузер настроен строго или страница
        // открыта в режиме без сохранения данных. Это не повод ломать страницу:
        // просто вид не запомнится между заходами.
        function remember(mode) {
            try {
                localStorage.setItem(STORAGE_KEY, mode);
            } catch (error) {
                /* пусть будет как есть */
            }
        }

        function restore() {
            try {
                return localStorage.getItem(STORAGE_KEY);
            } catch (error) {
                return null;
            }
        }

        apply(restore() === 'list' ? 'list' : 'grid');

        buttons.forEach(button => {
            button.addEventListener('click', () => {
                apply(button.dataset.view);
                remember(button.dataset.view);
            });
        });
    })();

    // ======================================================================
    // 8б. Боковое меню на узком экране
    //
    // На широком экране меню всегда на месте и этот код ничего не делает:
    // кнопка с тремя полосками там просто не видна (см. .burger в стилях).
    // На узком меню уезжает за левый край и выдвигается поверх страницы.
    // ======================================================================

    (function () {
        const sidebar = $('[data-sidebar]');
        const toggle = $('[data-sidebar-toggle]');
        const scrim = $('.scrim');

        if (!sidebar || !toggle) {
            return;
        }

        function setOpen(open) {
            sidebar.classList.toggle('is-open', open);
            toggle.setAttribute('aria-expanded', String(open));

            if (scrim) {
                scrim.classList.toggle('is-open', open);
            }
        }

        toggle.addEventListener('click', () => setOpen(!sidebar.classList.contains('is-open')));

        if (scrim) {
            scrim.addEventListener('click', () => setOpen(false));
        }

        // Нажатие по пункту меню тоже закрывает его: после перехода
        // на другую страницу открытое меню только мешает.
        sidebar.addEventListener('click', event => {
            if (event.target.closest('.nav-item')) {
                setOpen(false);
            }
        });

        document.addEventListener('keydown', event => {
            if (event.key === 'Escape') {
                setOpen(false);
            }
        });
    })();

    // ======================================================================
    // 8в. Поиск по Ctrl+K
    //
    // Сочетание клавиш переводит курсор в поле поиска в шапке, где бы
    // человек ни находился. Привычка из современных программ; подсказка
    // «Ctrl K» нарисована прямо в поле, чтобы про неё знали.
    // ======================================================================

    (function () {
        const field = $('[data-global-search]');

        if (!field) {
            return;
        }

        document.addEventListener('keydown', event => {
            // Проверяем и Ctrl, и Cmd — на случай, если портал откроют с Mac.
            // Браузерный поиск по странице это сочетание не занимает,
            // перехватывать безопасно.
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
                event.preventDefault();
                field.focus();
                field.select();
            }
        });
    })();

    // ======================================================================
    // 9. Колокольчик уведомлений
    //
    // Раз в минуту спрашиваем сервер, нет ли нового объявления. Постоянного
    // соединения нет намеренно: между офисами канал с урезанным MTU, и рвущийся
    // WebSocket выглядел бы как «портал завис». Один короткий запрос в минуту
    // на двадцать человек — это ничто.
    //
    // Что делаем с ответом:
    //   • число непрочитанного — на значок колокольчика;
    //   • список — в выпадающую панель под ним;
    //   • про то, что появилось ПРИ ОТКРЫТОЙ странице, — всплывающее
    //     сообщение справа внизу (только один раз на каждое событие).
    // ======================================================================

    (function () {
        const host = $('[data-bell]');

        if (!host) {
            return;
        }

        const toggle = $('[data-bell-toggle]', host);
        const badge = $('[data-bell-badge]', host);
        const panel = $('[data-bell-panel]', host);
        const list = $('[data-bell-list]', host);
        const clearButton = $('[data-bell-clear]', host);

        const POLL_MS = 60000;

        // Что уже показывали всплывающим сообщением — чтобы не показывать
        // одно и то же каждую минуту. Живёт до перезагрузки страницы.
        const announced = new Set();
        let first = true;

        /**
         * Число рядом с пунктом бокового меню.
         * Ноль не показываем совсем: пустой кружок только отвлекает.
         */
        function setNavCount(kind, value) {
            const element = $('[data-nav-count="' + kind + '"]');

            if (!element) {
                return;
            }

            element.hidden = !value;
            element.textContent = value > 99 ? '99+' : String(value);
        }

        function render(data) {
            const unread = data.unread || 0;

            badge.hidden = unread === 0;
            badge.textContent = unread > 99 ? '99+' : String(unread);

            toggle.classList.toggle('is-active', unread > 0);

            setNavCount('announcement', data.announcements || 0);
            setNavCount('message', data.messages || 0);

            list.innerHTML = '';

            if (!data.items || data.items.length === 0) {
                const empty = document.createElement('div');
                empty.className = 'bell__empty';
                empty.textContent = 'Нового нет';
                list.appendChild(empty);
                return;
            }

            data.items.forEach(item => {
                const link = document.createElement('a');
                link.className = 'bell__item';
                link.href = item.url;

                const title = document.createElement('strong');
                title.textContent = item.title;

                const meta = document.createElement('span');
                meta.textContent = item.author + ' · ' + formatWhen(item.at);

                link.appendChild(title);
                link.appendChild(meta);
                list.appendChild(link);
            });

            // При первой проверке всплывающих сообщений не показываем:
            // человек только что открыл страницу, и непрочитанное для него
            // не новость, а просто счётчик на колокольчике.
            if (!first) {
                data.items.forEach(item => {
                    const key = item.kind + ':' + item.id;

                    if (!announced.has(key)) {
                        announced.add(key);

                        toasts.show(
                            (item.kind === 'message' ? 'Новое сообщение: ' : 'Новое объявление: ')
                            + item.title,
                            'info');
                    }
                });
            } else {
                data.items.forEach(item => announced.add(item.kind + ':' + item.id));
                first = false;
            }
        }

        /** «Сегодня, 14:05» вместо полной даты — так читается быстрее. */
        function formatWhen(value) {
            const when = new Date(value);

            if (isNaN(when.getTime())) {
                return '';
            }

            const time = when.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
            const sameDay = when.toDateString() === new Date().toDateString();

            return sameDay
                ? 'сегодня, ' + time
                : when.toLocaleDateString('ru-RU') + ', ' + time;
        }

        let timer = null;
        let toldAboutSignOut = false;

        function stopPolling() {
            if (timer !== null) {
                clearInterval(timer);
                timer = null;
            }
        }

        function poll() {
            fetch('/api/notifications', {
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            })
                .then(response => (response.ok ? response.json() : Promise.reject(response.status)))
                .then(render)
                .catch(reason => {
                    // Истёкший вход — не «связь моргнула». Молчать про него
                    // нельзя: страница выглядит рабочей, а на деле не работает
                    // уже ничего. Говорим один раз и перестаём спрашивать —
                    // иначе сообщение всплывало бы каждую минуту.
                    if (reason === 401) {
                        stopPolling();
                        badge.hidden = true;
                        setNavCount('announcement', 0);
                        setNavCount('message', 0);

                        if (!toldAboutSignOut) {
                            toldAboutSignOut = true;

                            toasts.show(
                                'Вход в портал истёк. Обновите страницу и войдите заново.',
                                'error');
                        }

                        return;
                    }

                    // Всё остальное — молча: связь могла моргнуть, а ругаться
                    // на это всплывающим сообщением раз в минуту — издевательство.
                });
        }

        toggle.addEventListener('click', event => {
            event.stopPropagation();

            const open = panel.hidden;

            panel.hidden = !open;
            toggle.setAttribute('aria-expanded', String(open));
        });

        // Нажатие мимо панели её закрывает.
        document.addEventListener('click', event => {
            if (!panel.hidden && !event.target.closest('[data-bell]')) {
                panel.hidden = true;
                toggle.setAttribute('aria-expanded', 'false');
            }
        });

        clearButton.addEventListener('click', () => {
            fetch('/api/notifications/seen', {
                method: 'POST',
                headers: { 'X-Requested-With': 'XMLHttpRequest' }
            }).then(() => {
                panel.hidden = true;
                poll();
            });
        });

        poll();
        timer = setInterval(poll, POLL_MS);

        // Вернулись на вкладку — проверяем сразу, не дожидаясь минуты.
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible' && timer !== null) {
                poll();
            }
        });
    })();
})();
