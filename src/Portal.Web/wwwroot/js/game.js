/*
    Пасхалка портала: игра «Разбор завала» (wwwroot/game.html).

    Отдельным файлом, а не тегом <script> внутри страницы: политика
    безопасности запрещает встроенные скрипты, и браузер их не выполняет.

    Ни портала, ни сервера этот файл не касается: ни одного запроса
    наружу он не делает и ничего не сохраняет.
*/
(function () {
    'use strict';

    var canvas = document.getElementById('c');
    var ctx = canvas.getContext('2d');
    var W = canvas.width;
    var H = canvas.height;

    // Три цвета — фирменный синий портала, жёлтый папок и зелёный «готово».
    var COLORS = ['#0067c0', '#f2ae3c', '#1e8e5a'];
    var NAMES = ['Договоры', 'Приказы', 'Отчёты'];

    var basket = { x: W / 2, w: 96, h: 26, color: 0 };
    var papers = [];
    var score = 0;
    var lives = 3;
    var level = 1;
    var over = false;
    var spawnEvery = 78;
    var tick = 0;
    var left = false;
    var right = false;

    function reset() {
        papers = [];
        score = 0;
        lives = 3;
        level = 1;
        over = false;
        spawnEvery = 78;
        tick = 0;
        basket.x = W / 2;
        basket.color = 0;
    }

    function spawn() {
        papers.push({
            x: 30 + Math.random() * (W - 60),
            y: -24,
            color: Math.floor(Math.random() * COLORS.length),
            speed: 1.5 + level * 0.32 + Math.random() * 0.7,
            spin: (Math.random() - 0.5) * 0.05,
            angle: (Math.random() - 0.5) * 0.4
        });
    }

    function paper(p) {
        ctx.save();
        ctx.translate(p.x, p.y);
        ctx.rotate(p.angle);

        // Лист с загнутым уголком — узнаваемо и рисуется пятью линиями.
        ctx.fillStyle = COLORS[p.color];
        ctx.beginPath();
        ctx.moveTo(-11, -15);
        ctx.lineTo(5, -15);
        ctx.lineTo(11, -9);
        ctx.lineTo(11, 15);
        ctx.lineTo(-11, 15);
        ctx.closePath();
        ctx.fill();

        ctx.fillStyle = 'rgba(255,255,255,.55)';
        ctx.beginPath();
        ctx.moveTo(5, -15);
        ctx.lineTo(11, -9);
        ctx.lineTo(5, -9);
        ctx.closePath();
        ctx.fill();

        ctx.fillStyle = 'rgba(255,255,255,.75)';
        ctx.fillRect(-7, -4, 14, 2);
        ctx.fillRect(-7, 1, 14, 2);
        ctx.fillRect(-7, 6, 9, 2);

        ctx.restore();
    }

    function folder() {
        var y = H - 42;

        ctx.fillStyle = COLORS[basket.color];
        ctx.beginPath();
        ctx.roundRect(basket.x - basket.w / 2, y, basket.w, basket.h, 7);
        ctx.fill();

        // Язычок папки сверху слева — чтобы это читалось как папка.
        ctx.fillRect(basket.x - basket.w / 2, y - 7, 34, 10);

        ctx.fillStyle = '#fff';
        ctx.font = '600 11px Segoe UI, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText(NAMES[basket.color], basket.x, y + 18);
    }

    function step() {
        ctx.clearRect(0, 0, W, H);

        if (over) {
            ctx.fillStyle = 'rgba(0,0,0,.03)';
            ctx.fillRect(0, 0, W, H);

            ctx.textAlign = 'center';
            ctx.fillStyle = '#e5484d';
            ctx.font = '800 30px Segoe UI, sans-serif';
            ctx.fillText('Завал победил', W / 2, H / 2 - 12);

            ctx.fillStyle = 'rgba(128,128,128,.95)';
            ctx.font = '600 15px Segoe UI, sans-serif';
            ctx.fillText('Разобрано документов: ' + score + '. Пробел — ещё раз.', W / 2, H / 2 + 20);

            requestAnimationFrame(step);
            return;
        }

        tick++;

        if (tick % Math.max(22, spawnEvery - level * 4) === 0) {
            spawn();
        }

        if (left) { basket.x -= 7; }
        if (right) { basket.x += 7; }

        basket.x = Math.max(basket.w / 2, Math.min(W - basket.w / 2, basket.x));

        var catchY = H - 42;

        for (var i = papers.length - 1; i >= 0; i--) {
            var p = papers[i];

            p.y += p.speed;
            p.angle += p.spin;

            var inBasket = p.y + 15 >= catchY
                && p.y - 15 <= catchY + basket.h
                && Math.abs(p.x - basket.x) < basket.w / 2 + 8;

            if (inBasket) {
                papers.splice(i, 1);

                if (p.color === basket.color) {
                    score++;
                    level = 1 + Math.floor(score / 8);
                } else {
                    lives--;
                }

                continue;
            }

            if (p.y - 15 > H) {
                papers.splice(i, 1);
                lives--;
            }
        }

        if (lives <= 0) {
            lives = 0;
            over = true;
        }

        papers.forEach(paper);
        folder();

        document.getElementById('score').textContent = score;
        document.getElementById('lives').textContent = lives;
        document.getElementById('level').textContent = level;

        requestAnimationFrame(step);
    }

    canvas.addEventListener('mousemove', function (e) {
        var box = canvas.getBoundingClientRect();

        basket.x = (e.clientX - box.left) * (W / box.width);
    });

    // Нажатие по холсту меняет цвет папки — так можно играть одной мышью.
    canvas.addEventListener('click', function () {
        basket.color = (basket.color + 1) % COLORS.length;
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'ArrowLeft') { left = true; }
        if (e.key === 'ArrowRight') { right = true; }
        if (e.key === '1') { basket.color = 0; }
        if (e.key === '2') { basket.color = 1; }
        if (e.key === '3') { basket.color = 2; }

        if (e.key === ' ') {
            e.preventDefault();

            if (over) { reset(); }
        }
    });

    document.addEventListener('keyup', function (e) {
        if (e.key === 'ArrowLeft') { left = false; }
        if (e.key === 'ArrowRight') { right = false; }
    });

    // roundRect появился не во всех браузерах сразу — если его нет,
    // рисуем обычный прямоугольник. Игра от этого не страдает.
    if (!ctx.roundRect) {
        ctx.roundRect = function (x, y, w, h) {
            this.rect(x, y, w, h);
            return this;
        };
    }

    reset();
    step();
})();
