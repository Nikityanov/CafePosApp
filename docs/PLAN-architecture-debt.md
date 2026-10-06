# План: разбор архитектурного долга

Не план «сделать красиво». Каждый пункт отвечает на измеренную проблему, и каждый
заканчивается проверяемым признаком. Порядок жёсткий: ** god-класс не режут, пока
на нем нет тестов** — иначе это правка вслепую.

Все цифры сняты с дерева, а не оценены на глаз.

---

## 0. Что измерено (это и есть обоснование)

| Слой | кода (без комментариев) | файлов | покрыт тестами |
|---|---|---|---|
| ViewModels | 6 501 | 38 | **нет** |
| Core | 5 233 | 103 | да |
| Views (code-behind) | 1 448 | 18 | — |
| Services (MAUI) | 593 | 21 | — |
| **продукт** | **14 004** | **189** | **5 233 (37%)** |

**Дверь, за которую не доходят тесты.** `CafePos.Tests.csproj` ссылается только на
`CafePos.Core`. Все 6 501 строка ViewModel-ов недоступны. Это не гипотеза: сегодня
`ComboSlotRow` не собрался в тесте, и правило «степпер не ниже единицы» пришлось
проверять руками на устройстве — единственный случай за серию работ, где тест
написать было невозможно в принципе.

**Что при этом хорошо, и это меняет цену задачи.** Связь ViewModel-ов с MAUI — это
**только `Color`: 35 мест в 6 файлах**. Ни `Application.Current`, ни `Shell.Current`,
ни `Preferences`, ни `DisplayAlert` — ноль. Платформа уже отведена за интерфейсы
(`IDialogService`, `IHapticService`, `IFileService` в `Services/Abstractions/PlatformServices.cs`).
Значит дверь открывается дешевле, чем казалось.

**`MenuViewModel`.** Реальный класс размазан по трём файлам — 1 905 строк
(`Methods.cs` 771 + `Load.cs` 830 + `Checkout.cs` 304) — и принимает **16 зависимостей**.
Четвёртый файл, `MenuViewModel.cs` (641 строка), **не содержит `MenuViewModel`**: в нём
четыре посторонних класса — `CartItemViewModel`, `LineComponentViewModel`,
`MenuComboViewModel`, `CategoryMenuItemViewModel`. Файл назван по классу, которого в нём нет.

**`OrderService` — 1 159 строк в 5 файлах, но устроен нормально:** части по назначению
(чтение / команды / платежи / возвраты / отчётность). Настоящий дефект один и другой:
**`IOrderService` — плоский интерфейс на 23 метода с четырьмя разными поводами измениться**
(чтение заказов, команды, касса/смена, аналитика). Из-за него `ShiftAnalyticsViewModel`
зависит от того же интерфейса, что и `OrdersViewModel`, и оба могут дотянуться до
`CloseShiftAsync`. Это нарушение ISP, а не размер.

---

## 1. Открыть дверь: сделать ViewModel-ы тестируемыми

**Зачем первым.** Пока эта фаза не сделана, фазы 3 и 4 — резание кода без сетки.
Всё остальное в плане опирается на это.

**Куда положить ViewModel-ы.** Три варианта:

| | что | цена |
|---|---|---|
| **B. Новый проект `CafePos.Presentation`** | `Core` (домен) → `Presentation` (VM + порты) → `App` (MAUI). `Tests` → оба | новый csproj, перенос 38 файлов, 3 namespace'а. Честно по смыслу |
| A. Перенести в `Core` | ноль новых проектов | `Core` перестаёт значить «домен» и начинает значить «всё без UI». Дёшево, но врёт |
| C. `Compile Include` в Tests | ноль переносов | одна и та же компилируется дважды, ломается при каждой правке. Не делать |

**Рекомендация — B.** A даёт ту же тестируемость дешевле, но Core сейчас чистый
(103 файла, ноль ссылок на MAUI) и это надо беречь.

**Проблема, которую это вскрывает.** Порты `IDialogService` / `IHapticService` /
`IFileService` объявлены в MAUI-проекте. Домен и презентация должны **объявлять** порты,
а MAUI — **реализовывать**. Значит интерфейсы переезжают в `Presentation`, реализации
остаются в MAUI-проекте и регистрируются в DI как сейчас.

**`Color` — 35 мест в 6 файлах.** Правильное решение — убрать цвет из ViewModel-ов
совсем, а не вынести его в Core: ViewModel отдаёт **тон** (`ChipTone.Neutral`,
`.Accent`, `.Positive`, `.Warning`), а слой XAML отображает тон в цвет через
`AppThemeBinding`/конвертер. Это не только разблокирует тесты, но и убирает
оформление из бизнес-логики. Файлы: `MenuViewModel.cs`, `MenuViewModel.Methods.cs`,
`OrderDetailsViewModel.cs`, `OrdersViewModel.cs`, `SettingsViewModel.cs`,
`ShiftAnalyticsViewModel.cs`.

**Признак успеха:** `Tests` собирает презентацию; на первом же новом тесте закрывается
правило степпера комбо, о котором говорилось выше.

**Оценка:** 2–4 дня. Оценка грубая: перенос механический, `Color` — ручная работа в 6 файлах.

---

## 2. Разделить `MenuViewModel`

**Не переносить файл и не переименовывать — разобрать по обязанностям.** Из
поверхности класса видно восемь разных дел:

| выделить | что забирает | зависимости |
|---|---|---|
| `MenuCatalogue` | `LoadAsync`, `LoadCombosAsync`, `ApplyFilters`, `SelectCategory`, `HighlightSelectedChip` | `ICatalogService`, `IComboService` |
| `CartBuilder` | `AddItem`, `RemoveItem`, `Recalculate`, склейка строк, `UndoRemove` | почти никаких |
| `CompositionResolver` | `AddComboAsync`, `DescribeBundle`, `ResolveCompositionAsync`, `AddBundleLine`, отказ по недоступному блюду | `IComboService`, `IDialogService` |
| `FulfilmentEditor` | тип выдачи, телефон, обещанное время, `ResolveRequestedTime` | `IOrderTimePicker`, `TimeProvider` |
| `CheckoutCoordinator` | `PrepareCheckoutAsync`, `PayAndCreateAsync`, `HandleCheckoutFailureAsync`, `FinishOrderCreatedAsync`, `ParkOrderAsync`, `OpenParkedAsync` | `ICheckoutService`, `IPaymentSheet`, `IInventoryService` |
| `DraftAutosave` | `ScheduleAutoSave`, `AutoSaveAsync`, `RestoreDraftAsync` | `IDraftOrderService`, `TimeProvider` |

`MenuViewModel` после этого — тонкая оболочка: держит `IsBusy`, `TotalText`, склеивает
шесть Collaborator-ов и прокингивает команды. Ожидаемо **300–500 строк вместо 1 905**
и **5–8 зависимостей вместо 16**.

**Обязательно в том же проходе:** четыре класса-соседа выходят из `MenuViewModel.cs`
в свои файлы. Сегодня файл назван по классу, которого в нём нет, — это ломает и поиск,
и любую догадку о размере «этого ViewModel-а».

**Порядок работ внутри фазы (важно):**

1. **Сначала** characterisation-тесты на `MenuViewModel` как он есть сейчас: корзина,
   склейка строк, фильтр, `AddComboAsync` (включая отказ по недоступному компоненту),
   обещанное время. Это страховка: пока класс цел, тесты описывают его поведение.
2. Потом выносить по одному Collaborator-у, после каждого — прогон тестов.
3. `CartBuilder` и `CompositionResolver` первыми: у них наименьшая поверхность и самые
   частые падения. `CheckoutCoordinator` последним — он самый дорогой и самый
   чувствительный к порядку вызовов.

**Признак успеха:** нет файла больше ~500 строк; у `MenuViewModel` ≤8 зависимостей;
тесты пережили разбор без правки ожиданий (только добавления).

**Оценка:** 4–6 дней.

---

## 3. `IOrderService`: разделить интерфейс, не класс

Класс трогать не нужно — он уже разложен правильно. Работа только по сигнатуре и
потребителям:

| интерфейс | методы |
|---|---|
| `IOrderQueries` | `GetOrderAsync`, `GetActiveOrdersAsync`, `GetCompletedOrdersAsync`, `GetShiftOrderHistoryAsync`, `GetStatusHistoryAsync` |
| `IOrderCommands` | `AdvanceStatusAsync`, `CancelOrderAsync`, `UpdateOrderAsync`, `MarkSeenAsync`, `AddContactDetailsAsync` |
| `IShiftLedger` | `GetActiveShiftAsync`, `OpenShiftAsync`, `GetLastCountedCashKopecksAsync`, `GetLatestShiftAsync`, `CloseShiftAsync` |
| `IOrderPayments` | `AddPaymentAsync`, `GetOrderPaymentsAsync`, `RefundAsync` |
| `IOrderReporting` | `GetShiftStatsAsync`, `GetProductAnalyticsAsync`, `GetDiscountedLinesAsync` |

`OrderService` реализует все пять — **поведение не меняется ни на строку**, это чистое
разделение интерфейса. Потребители потом зависят только от своего:
`OrdersViewModel` → `Queries` + `Commands`; `ShiftAnalyticsViewModel` → `Reporting`;
кто работает со сменой → `ShiftLedger`.

`IOrderService` оставить как `: IOrderQueries, IOrderCommands, IShiftLedger,
IOrderPayments, IOrderReporting` — чтобы существующий код не ломался целиком и миграция
шла по одному потребителю.

**Признак успеха:** `ShiftAnalyticsViewModel` физически не может вызвать `CloseShiftAsync`.

**Оценка:** 1 день, риск близок к нулю. **Можно делать первым** — он не зависит ни от
фазы 1, ни от фазы 2, и сразу честно сужает граф зависимостей.

---

## 4. Мелкие god-файлы (после 1–3)

| файл | строк | что делать |
|---|---|---|
| `ComboFormViewModel` | 889 / 2 | форма и валидация — отдельно; работа со слотом — отдельно |
| `ComboSlotRow` | 639 / 1 | вынести шаг количества и применение режима цены |
| `CatalogService` | 542 / 3 | посмотреть на границы после разделения `IOrderService` — возможно, часть уходит в отдельный сервис |
| `ProductFormViewModel` | 510 / 3 | то же, что `ComboFormViewModel` |
| `Tests/OrderPaymentTests.cs` | 1 139 | разбить по сценариям; файл тестов растёт так же, как код |

`ProductAnalyticsProjection.cs` (378) — проекция, скорее всего в порядке; смотреть только
если придётся трогать.

---

## Чего план НЕ делает

- **Не трогает ленту категорий** — чипы сжимаются вместо переполнения на узком экране
  (S23+, Android 16). Это самостоятельный дефект, найденный при установке на телефон,
  к архитектуре отношения не имеет.
- **Не переименовывает `Core`.** Пока `Core` = домен, это верно; если выбрать вариант A
  из фазы 1, переименование становится обязательным, иначе имя соврёт.
- **Не вводит новых зависимостей.** Нужен только `CommunityToolkit.Mvvm`, который уже есть.

---

## Порядок одной строкой

**3 → 1 → (тесты на MenuViewModel) → 2 → 4.**

Фаза 3 бесплатна и сразу улучшает граф. Фаза 1 открывает сетку. Только после этого
режут `MenuViewModel`, и только после этого — остальное.