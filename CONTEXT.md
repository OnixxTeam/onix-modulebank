# Course Action Runtime

Учебный контур ModuleDev: обобщённый C# runtime публикует зарегистрированные PostgreSQL-функции
как HTTP actions (неделя 1) и исполняет версионированные процессы поверх них (неделя 2). Ключевая
особенность контекста — ни одно предметное имя не зашито в C#: действия и карты процессов
публикуются данными и исполняются без пересборки.

## Language

### Action runtime

**Action**:
Опубликованная единица предметной работы: манифест плюс целевая PostgreSQL-функция, вызываемая
через общий маршрут.
_Avoid_: Endpoint, handler, команда

**Manifest**:
Запись каталога, объявляющая контракт action — schemas, outcomes, policy, целевую функцию.
_Avoid_: Definition, конфиг, метаданные

**Outcome**:
Объявленное предметное завершение вызова action в верхнем регистре. Не является статусом
транспорта и не является ошибкой.
_Avoid_: Status, result code, state

**Policy**:
Требуемое право в форме `scope:verb`, которое principal обязан иметь для вызова action.
_Avoid_: Permission, role, право доступа

**Principal**:
Субъект вызова, чьи scopes проверяются на стороне сервера. `workflow-worker` — принципал,
под которым воркер вызывает actions.
_Avoid_: User, client, актор

**Dispatch**:
Запись факта вызова action: кто, что, с каким результатом. Фиксируется в той же транзакции,
что и предметный эффект.
_Avoid_: Log, audit record, trace

### Определение процесса

**Flow**:
Именованный процесс. Стабильное имя, за которым стоит набор версий.
_Avoid_: Workflow, процесс, сценарий

**Flow version**:
Неизменяемая опубликованная версия карты. После публикации не меняется никогда.
_Avoid_: Revision, релиз, снапшот

**Map**:
Документ `course-1`, описывающий flow version: steps, transitions и tasks. Единица публикации.
_Avoid_: Definition file, схема процесса, BPMN

**Step**:
Узел карты одного из четырёх типов: `automatic`, `wait_signal`, `manual`, `end`.
_Avoid_: Node, stage, этап

**Route**:
Переход между шагами, выбираемый по конкретному outcome.
_Avoid_: Edge, branch, ветка

**Task**:
Закреплённый в шаге `automatic` контракт вызова action вместе с policy, timeout, retry и mapping.
_Avoid_: Job, действие, вызов

**Pinned version**:
Версия карты, к которой навсегда привязан уже запущенный процесс. Активация новой версии на него
не влияет.
_Avoid_: Current version, зафиксированная версия

**Active version**:
Единственная published-версия flow, выбираемая для новых процессов.
_Avoid_: Latest, текущая, default

### Исполнение

**Process**:
Экземпляр конкретной flow version с собственным состоянием и историей.
_Avoid_: Instance, run, execution

**Business key**:
Внешний ключ идемпотентности запуска: пара flow плюс business key даёт не более одного процесса.
_Avoid_: Correlation key, идентификатор заявки

**Job**:
Единица работы, порождаемая шагом `automatic` и подлежащая захвату воркером.
_Avoid_: Task, work item, очередь

**Attempt**:
Одна попытка исполнения job. Строки попыток только добавляются.
_Avoid_: Try, run, итерация

**Signal**:
Идемпотентно принятое внешнее событие, продвигающее процесс из шага `wait_signal`.
_Avoid_: Message, event, уведомление

**Event**:
Строка append-only истории процесса. История дополняется и никогда не переписывается.
_Avoid_: Log, audit, changelog

**Start data**:
Неизменяемый JSON, переданный `flow start --data`. Ключ идемпотентности запуска сравнивается
только по нему; после старта не меняется никогда.
_Avoid_: Input, payload, request body

**Process data**:
Накопленное состояние процесса: при старте — копия start data, дальше на каждый завершённый
`automatic`-шаг под его собственным step key добавляется результат. Источник `input_mapping`
(TASK.md "Mapping": "source process data pointer") — первый шаг может читать start data через
него ровно потому, что она уже там при создании процесса.
_Avoid_: State, context, variables

### Аренда и разграничение

**Lease**:
Временное право воркера работать над job. Истекает по сроку, heartbeat не предусмотрен.
_Avoid_: Lock, аренда, блокировка

**Lease version**:
Возрастающий счётчик права завершить job. Защищает завершение, а не исполнение.
_Avoid_: Fencing token, версия блокировки

**Execution id**:
Стабильный ключ идемпотентности предметного эффекта, общий для всех попыток одного job.
_Avoid_: Idempotency key, request id, correlation id

**Attempt id**:
Идентификатор конкретной попытки. Не годится как ключ предметного эффекта, потому что меняется
на каждом повторе.
_Avoid_: Run id, execution id

**Reclaim**:
Перехват job после истечения lease: тот же job и тот же execution id, новая попытка и новая
lease version.
_Avoid_: Retry, перезахват, recovery

**Stale finish**:
Попытка завершить job владельцем устаревшей lease. Отклоняется и не меняет ничего.
_Avoid_: Late commit, конфликт, гонка

**Failpoint**:
Именованная граница исполнения, на которой воркер в тестовом профиле объявляет о достижении
и блокируется. Существует для воспроизводимых аварийных сценариев.
_Avoid_: Breakpoint, chaos hook, точка отказа
