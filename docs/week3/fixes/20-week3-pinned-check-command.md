# Тикет 20. Заменить Week 2 wrapper воспроизводимой pinned Week 3 командой проверки

- **Ветка:** `feat/w3-20-week3-pinned-check-command`
- **Приоритет:** средний (не блокирует зачёт, замечание сохраняется)
- **Источник:** `week-3-intern-feedback.pdf`, «Средний приоритет: Локальная команда проверки относится к Week 2»
- **Порядок доработки:** 5 из 5 (последний)

## Наблюдение

`README.md:251-270` объявляет `./check.sh` открытой black-box проверкой
**текущего** задания.

Фактически `check.sh` → `autocheck/check.sh:16-21` запускает
`autocheck/public_check.py`, у которого
`MANIFEST_VERSION = "week-2-public-report/v1"` и
`TOOL_VERSION = "week-2-public-check/1.1"`
(`autocheck/public_check.py:29-30`), а результат пишется в
`week-2-public-report.json` (`autocheck/check.sh:20`).

В корне репозитория при этом лежит `week-3-public-report.json` с
`toolVersion: "week-3-public-check/0.3"` — отчёт стороннего прогона, самого
чекера в дереве нет.

## Последствие

Разработчик получает зелёный отчёт прошлой недели, не проверив provider,
Python-сервисы, Outbox/Inbox, signed receipt и manual process текущего
инкремента. Расхождение README и tracked wrapper вводит в заблуждение и
самого автора, и проверяющего.

## Что сделать

1. Вендорить pinned Week 3 checker в `autocheck/` (либо самодостаточный
   эквивалент, покрывающий week-3 contracts):
   `MANIFEST_VERSION = "week-3-public-report/v1"`, зафиксированная
   `TOOL_VERSION` (не ниже `week-3-public-check/0.3`), собственный
   `PUBLISHED_FIXTURE_DIGEST`.
2. `autocheck/check.sh` пишет `--output "$repo_dir/week-3-public-report.json"`
   и подхватывает week-3 fixtures.
3. Week 2 checker сохранить как архив (по образцу существующего `oldcheck/`:
   переместить в `oldcheck/week2/`), чтобы прошлый инкремент оставался
   воспроизводимым, но не был дефолтом.
4. `REQUIRED_SERVICES` расширить до week-3 набора: `gateway`, `api`, `cli`,
   `postgres`, `workflow-worker`, `outbox-dispatcher`, `receipt-adapter`,
   `inbox-reconciler`, `provider-simulator`, `migrator` — всего десять
   сервисов.
5. `README.md:251-270` привести в соответствие: одна документированная
   команда, имя отчёта week-3, никакого упоминания week-2 wrapper как
   «проверки текущего задания».

## Как проверить

На **чистом клоне** одна документированная команда:

```bash
./check.sh
```

должна:

- поднять десять сервисов без ручных шагов;
- проверить Week 3 contracts (provider success/reject/lost-response/early/
  transient, Outbox stop/resume, duplicate/conflict, reconciler recreate,
  20-way submit/manual, malformed callback matrix, exact signed body,
  финальная история operation);
- выдать `week-3-public-report.json` с week-3 `manifestVersion` и pinned
  `toolVersion`;
- повторный прогон на том же commit даёт тот же набор проверок (пины
  зафиксированы, ничего не тянется из сети без digest).

## Готово, когда

`./check.sh` на чистом клоне выдаёт зелёный `week-3-public-report.json`,
`week-2-public-report.json` больше не создаётся дефолтной командой,
README описывает ровно её.
