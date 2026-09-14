using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcBusBot.Core.Models;

// ─────────────────────────────────────────────────────────────
//  TDX 公車 v2 API 的資料模型
//  欄位名稱刻意與 TDX JSON 一致（PascalCase），少一層對應就少一個錯字機會。
//  ⚠️ EstimateTime / NextBusTime 等欄位在 TDX 回應中「可能整個消失」，
//     所以一律用可空型別（見 docs/01 研究報告 §4.3）。
// ─────────────────────────────────────────────────────────────

/// <summary>TDX 的本地化名稱物件：{"Zh_tw": "...", "En": "..."}</summary>
public sealed record LocalizedName(
    [property: JsonPropertyName("Zh_tw")] string? ZhTw,
    [property: JsonPropertyName("En")] string? En)
{
    public override string ToString() => ZhTw ?? En ?? "";
}

public sealed record StopPosition(
    [property: JsonPropertyName("PositionLon")] double PositionLon,
    [property: JsonPropertyName("PositionLat")] double PositionLat,
    [property: JsonPropertyName("GeoHash")] string? GeoHash);

/// <summary>GET /v2/Bus/Stop/City/Taichung 的單筆資料</summary>
public sealed class BusStop
{
    public string StopUID { get; init; } = "";
    public string StopID { get; init; } = "";
    public LocalizedName? StopName { get; init; }
    public StopPosition? StopPosition { get; init; }
    public string? Bearing { get; init; }
    public string? StationID { get; init; }
    public string? StationGroupID { get; init; }
    public string? City { get; init; }
    public string? CityCode { get; init; }

    [JsonIgnore] public string ZhTwName => StopName?.ZhTw ?? StopUID;
    [JsonIgnore] public string EnName => StopName?.En ?? "";
    [JsonIgnore] public double Lon => StopPosition?.PositionLon ?? 0;
    [JsonIgnore] public double Lat => StopPosition?.PositionLat ?? 0;

    public override string ToString() => $"{StopUID} {ZhTwName}";
}

/// <summary>StopOfRoute 內的一個站點（含站序與座標）</summary>
public sealed class StopOfRouteStop
{
    public string StopUID { get; init; } = "";
    public string StopID { get; init; } = "";
    public LocalizedName? StopName { get; init; }
    public int StopBoarding { get; init; }
    public int StopSequence { get; init; }
    public StopPosition? StopPosition { get; init; }
    public string? StationID { get; init; }
    public string? StationGroupID { get; init; }

    [JsonIgnore] public string ZhTwName => StopName?.ZhTw ?? StopUID;
}

/// <summary>
/// GET /v2/Bus/StopOfRoute/City/Taichung 的單筆資料。
/// 陣列中每個 (SubRouteUID, Direction) 是一筆獨立元素。
/// </summary>
public sealed class BusStopOfRoute
{
    public string RouteUID { get; init; } = "";
    public string RouteID { get; init; } = "";
    public LocalizedName? RouteName { get; init; }
    public string SubRouteUID { get; init; } = "";
    public string SubRouteID { get; init; } = "";
    public LocalizedName? SubRouteName { get; init; }
    public int Direction { get; init; }
    public List<StopOfRouteStop> Stops { get; init; } = new();
    public string? City { get; init; }
    public string? CityCode { get; init; }

    [JsonIgnore] public string RouteZhTwName => RouteName?.ZhTw ?? RouteID;
}

public sealed class BusSubRoute
{
    public string SubRouteUID { get; init; } = "";
    public string SubRouteID { get; init; } = "";
    public LocalizedName? SubRouteName { get; init; }
    public int Direction { get; init; }
    public string? Headsign { get; init; }
    public string? HeadsignEn { get; init; }
}

/// <summary>GET /v2/Bus/Route/City/Taichung 的單筆資料（只取本專案會用到的欄位）</summary>
public sealed class BusRoute
{
    public string RouteUID { get; init; } = "";
    public string RouteID { get; init; } = "";
    public LocalizedName? RouteName { get; init; }
    public bool HasSubRoutes { get; init; }
    public List<BusSubRoute> SubRoutes { get; init; } = new();
    public string? DepartureStopNameZh { get; init; }
    public string? DestinationStopNameZh { get; init; }
    public string? City { get; init; }
    public string? CityCode { get; init; }

    [JsonIgnore] public string RouteZhTwName => RouteName?.ZhTw ?? RouteID;
}

// ─────────────────────────────────────────────────────────────
//  即時資料（N1 預估到站）
// ─────────────────────────────────────────────────────────────

/// <summary>ETA 回應中的 Estimates[] 元素（同一站同一路線可能有多班車）</summary>
public sealed class EtaItem
{
    public string? PlateNumb { get; init; }
    public int? EstimateTime { get; init; }

    /// <summary>
    /// TDX 資料標準寫 0/1，但實際 JSON 可能是 true/false。
    /// 用 FlexibleBoolConverter 兩種都吃。
    /// </summary>
    [JsonConverter(typeof(FlexibleBoolConverter))]
    public bool? IsLastBus { get; init; }
}

/// <summary>GET /v2/Bus/EstimatedTimeOfArrival/City/Taichung 的單筆資料</summary>
public sealed class BusEta
{
    public string? PlateNumb { get; init; }
    public string StopUID { get; init; } = "";
    public string StopID { get; init; } = "";
    public LocalizedName? StopName { get; init; }
    public string RouteUID { get; init; } = "";
    public string RouteID { get; init; } = "";
    public LocalizedName? RouteName { get; init; }
    public string SubRouteUID { get; init; } = "";
    public string SubRouteID { get; init; } = "";
    public LocalizedName? SubRouteName { get; init; }
    public int Direction { get; init; }
    public int StopSequence { get; init; }

    /// <summary>到站時間預估（秒）。StopStatus 為 2~4 或 PlateNumb 為 -1 時為 null。</summary>
    public int? EstimateTime { get; init; }

    /// <summary>0=正常 1=尚未發車 2=交管不停靠 3=末班車已過 4=今日未營運（標準另有 5=其他）</summary>
    public int StopStatus { get; init; }

    public DateTimeOffset? NextBusTime { get; init; }
    public List<EtaItem> Estimates { get; init; } = new();
    public DateTimeOffset SrcUpdateTime { get; init; }
    public DateTimeOffset UpdateTime { get; init; }

    [JsonIgnore] public bool HasEstimate => EstimateTime.HasValue;

    /// <summary>PlateNumb 為 -1 或空字串代表「無車輛」（官方 OAS 明載）。</summary>
    [JsonIgnore] public bool HasVehicle =>
        !string.IsNullOrEmpty(PlateNumb) && PlateNumb != "-1";

    public override string ToString() =>
        $"{RouteUID}/{Direction} @{StopUID} status={StopStatus} eta={EstimateTime?.ToString() ?? "null"}";
}

// ─────────────────────────────────────────────────────────────
//  容錯型別轉換
// ─────────────────────────────────────────────────────────────

/// <summary>
/// 同時接受 true/false、0/1、以及字串 "0"/"1"/"true"/"false" 的 bool 轉換器。
/// TDX 資料標準與實際 JSON 在此欄位不一致，硬轉會炸。
/// </summary>
public sealed class FlexibleBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetInt32() != 0,
            JsonTokenType.String => reader.GetString() switch
            {
                "1" or "true" or "True" or "TRUE" => true,
                "0" or "false" or "False" or "FALSE" => false,
                _ => null
            },
            _ => null
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteBooleanValue(value.Value);
    }
}
