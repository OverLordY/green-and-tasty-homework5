# Green & Tasty — Backend: груповий pre-order для Owner

Домашнє завдання №5 EPAM IT Marathon 6.0. Реалізовано всі три рівні: перегляд учасників, підтвердження спільного замовлення та видалення гостьової сесії.

| Метод | Endpoint | Рівень |
| --- | --- | --- |
| GET | `/guest/groups/{groupReservationId}/sessions` | 1 |
| GET | `/reservations/{reservationId}/guest-session-id` | 2 |
| POST | `/group-reservations/{groupReservationId}/submit` | 2 |
| DELETE | `/guest/groups/{groupReservationId}/sessions/{guestSessionId}` | 3 |

Owner визначається за підтвердженим OIDC/JWT та звіряється з власником бронювання. Суми розраховуються на сервері з `decimal`. Під час підтвердження враховуються лише submitted-сесії, однакові страви об’єднуються за `dishId`, а зміни статусів зберігаються атомарно.

## Запуск тестів

Потрібен .NET 8 SDK:

```bash
dotnet test backend/Restaurant.Api.Tests/Restaurant.Api.Tests.csproj -c Release
```

Тести охоплюють контракти HTTP, права Owner, підрахунок, помилки, статуси та DynamoDB-транзакції. Остання локальна перевірка: 38 тестів успішно. Повна перевірка розгорнутого API — окремий етап, див. звіт після її проведення.

## Структура

- `backend/Restaurant.Api/` — ASP.NET Core API та робота з DynamoDB.
- `backend/Restaurant.Api.Tests/` — автоматичні тести.
- `backend/infra/` — CloudFormation і скрипт підготовки розгортання.
- `homework-backend.md` — початкові вимоги.
- `agents/` — інструкції AI-агентів, використані під час виконання.

Оригінальний Backend у навчальному архіві був відсутній; цей проєкт є самостійною реалізацією контракту. Для реального запуску потрібні налаштовані OIDC і сховище DynamoDB. Непідписаний demo-токен Frontend тут не використовується. Детальна конфігурація та межі функціоналу описані в [backend/README.md](backend/README.md).

## Використання AI

AI-агенти допомагали аналізувати умови, писати й перевіряти код. Результат перевірено збіркою, автоматичними тестами та окремим оглядом реалізації.
