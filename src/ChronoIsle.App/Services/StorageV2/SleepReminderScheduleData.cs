using Microsoft.Data.Sqlite;

namespace ChronoIsle.App.Services;

public sealed partial class LifeDataService
{
    /// <summary>
    /// Creates the two parts of an official-calendar sleep schedule atomically:
    /// official workdays and statutory holidays. The calendar itself remains a
    /// local, versioned source; no network request is made while scheduling.
    /// </summary>
    public IReadOnlyList<RecurringReminder> SaveOfficialSleepReminderSchedule(
        TimeOnly officialWorkdayTime,
        TimeOnly statutoryHolidayTime)
    {
        var now = localNow();
        var reminders = new[]
        {
            new RecurringReminder(
                Guid.NewGuid().ToString("N"),
                "睡觉提醒（工作日）",
                "按国务院法定节假日与调休上班日安排。",
                officialWorkdayTime,
                RecurrenceKind.OfficialWorkdays,
                [],
                null,
                now,
                now),
            new RecurringReminder(
                Guid.NewGuid().ToString("N"),
                "睡觉提醒（法定节假日）",
                "按法定节假日与周末安排，调休上班日除外。",
                statutoryHolidayTime,
                RecurrenceKind.StatutoryHolidays,
                [],
                null,
                now,
                now)
        };
        var nextOccurrences = reminders
            .Select(reminder => NextOccurrence(reminder, now)
                ?? throw new InvalidOperationException("当前已接入的法定节假日日历中没有可安排的下一次提醒。"))
            .ToList();

        writeQueue.Execute(unitOfWork =>
        {
            for (var index = 0; index < reminders.Length; index++)
            {
                var reminder = reminders[index];
                using var command = unitOfWork.Connection.CreateCommand();
                command.Transaction = unitOfWork.Transaction;
                command.CommandText = """
                    INSERT INTO recurring_reminders(id,title,notes,reminder_time,recurrence,weekdays,last_notified_at,created_at,updated_at)
                    VALUES($id,$title,$notes,$time,$recurrence,$weekdays,NULL,$created,$updated)
                    """;
                BindRecurring(command, reminder);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("睡觉提醒没有写入本地数据库。");
                canonicalWriter.UpsertRecurringReminder(
                    unitOfWork.Connection,
                    unitOfWork.Transaction,
                    reminder,
                    nextOccurrences[index].StartsAt);
            }
        });
        RaiseAgendaChanged();
        return reminders;
    }
}
