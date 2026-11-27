use chrono::{DateTime, Duration, NaiveDate, NaiveDateTime, NaiveTime, Utc};

use crate::limits::RESET_HOUR;

/// Every policy rule (day and week resets, play windows, clock times shown to
/// the user) runs on a fixed UTC-05 clock. The PC's time zone and DST settings
/// do not change when a day starts or which play window applies.
pub struct PolicyClock;

impl PolicyClock {
    pub const OFFSET_HOURS: i64 = -5;
    pub const LABEL: &'static str = "UTC-05";

    pub fn offset() -> Duration {
        Duration::hours(Self::OFFSET_HOURS)
    }

    pub fn from_utc(utc: DateTime<Utc>) -> NaiveDateTime {
        (utc + Self::offset()).naive_utc()
    }

    pub fn to_utc(policy_time: NaiveDateTime) -> DateTime<Utc> {
        DateTime::<Utc>::from_naive_utc_and_offset(policy_time - Self::offset(), Utc)
    }

    pub fn now() -> NaiveDateTime {
        Self::from_utc(Utc::now())
    }
}

/// A logical day runs 00:00 -> 00:00 on the policy clock (UTC-05).
pub struct LogicalDay;

impl LogicalDay {
    pub fn parse(value: &str) -> Option<NaiveDate> {
        NaiveDate::parse_from_str(value, "%Y-%m-%d").ok()
    }

    pub fn format(date: NaiveDate) -> String {
        date.format("%Y-%m-%d").to_string()
    }

    pub fn date_for_policy(policy_time: NaiveDateTime) -> NaiveDate {
        (policy_time - Duration::hours(i64::from(RESET_HOUR))).date()
    }

    pub fn for_policy(policy_time: NaiveDateTime) -> String {
        Self::format(Self::date_for_policy(policy_time))
    }

    pub fn for_utc(utc: DateTime<Utc>) -> String {
        Self::for_policy(PolicyClock::from_utc(utc))
    }

    pub fn next_reset(policy_time: NaiveDateTime) -> NaiveDateTime {
        let reset_time = NaiveTime::from_hms_opt(RESET_HOUR, 0, 0).expect("valid reset hour");
        let today_reset = policy_time.date().and_time(reset_time);
        if policy_time < today_reset {
            today_reset
        } else {
            today_reset + Duration::days(1)
        }
    }
}
