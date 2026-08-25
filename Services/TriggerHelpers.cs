using Newtonsoft.Json.Linq;
using SuperTrigger.Web.Data;
using SuperTrigger.Web.Data.Entities;

namespace SuperTrigger.Web.Services;

public static class TriggerHelpers
{
    public static string BuildFolderPath(OrchSettings settings, IFolderTrigger trigger)
    {
        var parts = new List<string> { settings.OrchestratorMainFolderName };
        if (!string.IsNullOrEmpty(trigger.DivisionName))
        {
            parts.Add(trigger.DivisionName);
            parts.Add(trigger.CompanyName);
        }
        parts.Add(trigger.BusinessDepartmentName);
        parts.Add(trigger.BusinessProcessName);
        return string.Join("/", parts);
    }

    public static string? ToLocal(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;

        // Newtonsoft turns ISO timestamps into Date tokens, so ToString() would drop
        // the "Z" and leave us parsing a Kind=Unspecified value that never converts.
        if (token is JValue { Value: DateTimeOffset dto })
            return FormatLocal(dto.UtcDateTime);
        if (token is JValue { Value: DateTime dt })
            return FormatLocal(dt);

        return ToLocal(token.ToString());
    }

    public static string? ToLocal(string? graphDateTime)
    {
        if (string.IsNullOrEmpty(graphDateTime)) return graphDateTime;
        try
        {
            return FormatLocal(DateTime.Parse(graphDateTime, null, System.Globalization.DateTimeStyles.RoundtripKind));
        }
        catch { return graphDateTime; }
    }

    private static string FormatLocal(DateTime dt)
    {
        // Graph returns UTC for receivedDateTime, and for event start/end as long as we
        // send no "Prefer: outlook.timezone" header - so an unspecified Kind is UTC too.
        if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return dt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    }

    public static bool MatchesMailFilters(JObject message, MailTrigger trigger)
    {
        var subject = message["subject"]?.ToString() ?? "";
        var fromEmail = message["from"]?["emailAddress"]?["address"]?.ToString() ?? "";
        var bodyContent = message["body"]?["content"]?.ToString() ?? "";

        if (!string.IsNullOrEmpty(trigger.SubjectFilterContains) &&
            !subject.Contains(trigger.SubjectFilterContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(trigger.BodyFilterContains) &&
            !bodyContent.Contains(trigger.BodyFilterContains, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrEmpty(trigger.From))
        {
            var fromFilters = trigger.From.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fromFilters.Length > 0 &&
                !fromFilters.Any(f => fromEmail.Contains(f, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    public static JObject BuildGraphMailPayload(JObject message, MailTrigger trigger)
    {
        var subject = message["subject"]?.ToString() ?? "";
        var from = message["from"]?["emailAddress"]?["address"]?.ToString() ?? "";
        var received = ToLocal(message["receivedDateTime"]) ?? "";

        var raw = $"{received}_{subject}_{from}";
        var reference = raw[..Math.Min(128, raw.Length)];

        var meetingType = message["meetingMessageType"]?.ToString() ?? "none";
        var isMeetingRequest = meetingType == "meetingRequest";
        var isMeetingResponse = meetingType is "meetingAccepted" or "meetingTentativelyAccepted" or "meetingDeclined";
        var isMeetingCancellation = meetingType == "meetingCancelled";

        var content = new JObject
        {
            ["TriggerName"] = trigger.TriggerName,
            ["MailUID_365"] = message["id"]?.ToString(),
            ["MailId"] = message["id"]?.ToString()?.Replace('_', '+').Replace('-', '/'),
            ["MailInternetUID"] = message["internetMessageId"]?.ToString(),
            ["MailFolder"] = trigger.MailFolder,
            ["MailSharedBox"] = trigger.SharedMailBox,
            ["Sender"] = from,
            ["Subject"] = subject,
            ["DateTimeReceived"] = received,
            ["HasAttachments"] = message["hasAttachments"]?.ToString(),
            ["IsMeetingRequest"] = isMeetingRequest.ToString(),
            ["IsMeetingResponse"] = isMeetingResponse.ToString(),
            ["IsMeetingCancellation"] = isMeetingCancellation.ToString()
        };

        if (isMeetingRequest || isMeetingResponse || isMeetingCancellation)
        {
            var evt = message["event"] as JObject;
            var appointmentId = evt?["iCalUId"]?.ToString() ?? evt?["id"]?.ToString();
            if (!string.IsNullOrEmpty(appointmentId))
                content["AppointmentId"] = appointmentId;

            if (isMeetingRequest)
            {
                content["MeetingRequestType"] = meetingType;
                content["MeetingStart"] = ToLocal(evt?["start"]?["dateTime"]);
                content["MeetingEnd"] = ToLocal(evt?["end"]?["dateTime"]);
                var location = evt?["location"]?["displayName"]?.ToString();
                if (!string.IsNullOrEmpty(location)) content["MeetingLocation"] = location;
                var evtType = evt?["type"]?.ToString() ?? "";
                var isRecurring = evtType is "seriesMaster" or "occurrence" or "exception";
                content["MeetingIsRecurring"] = isRecurring.ToString();
                if (isRecurring && evt?["recurrence"] != null)
                    content["MeetingRecurrence"] = evt["recurrence"]!.ToString(Newtonsoft.Json.Formatting.None);
            }
            else if (isMeetingResponse)
            {
                content["MeetingResponseType"] = meetingType;
            }
        }

        return new JObject
        {
            ["itemData"] = new JObject
            {
                ["Reference"] = reference,
                ["SpecificContent"] = content
            }
        };
    }
}
