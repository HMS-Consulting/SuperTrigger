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

    public static string? ToLocal(string? graphDateTime)
    {
        if (string.IsNullOrEmpty(graphDateTime)) return graphDateTime;
        try
        {
            var dt = DateTime.Parse(graphDateTime, null, System.Globalization.DateTimeStyles.RoundtripKind);
            if (dt.Kind == DateTimeKind.Utc) dt = dt.ToLocalTime();
            return dt.ToString("dd/MM/yyyy HH:mm:ss");
        }
        catch { return graphDateTime; }
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
        var received = ToLocal(message["receivedDateTime"]?.ToString()) ?? "";

        var raw = $"{received}_{subject}_{from}";
        var reference = raw[..Math.Min(128, raw.Length)];

        var meetingType = message["meetingMessageType"]?.ToString() ?? "none";
        var isMeetingRequest = meetingType == "meetingRequest";
        var isMeetingResponse = meetingType is "meetingAccepted" or "meetingTentativelyAccepted" or "meetingDeclined";
        var isMeetingCancellation = meetingType == "meetingCancelled";

        var content = new JObject
        {
            ["TriggerName"] = trigger.TriggerName,
            ["MailId"] = message["id"]?.ToString(),
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
                content["MeetingStart"] = ToLocal(evt?["start"]?["dateTime"]?.ToString());
                content["MeetingEnd"] = ToLocal(evt?["end"]?["dateTime"]?.ToString());
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
