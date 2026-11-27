use std::collections::{BTreeMap, HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};

use chrono::{DateTime, Datelike, Duration, FixedOffset, Utc};
use serde::{Deserialize, Serialize};
use tracing::{error, warn};

use crate::clock::{LogicalDay, PolicyClock};
use crate::limits;
use crate::state::atomic_write_durable;

pub const SIMPLE_HEALTH_SOURCE: &str = "com.mc.simplehealth";
pub const WORKOUT_PAGE_SIZE: usize = 8;

/// Format a UTC instant with 7 fractional digits (matching .NET's `"O"` format)
/// so heart-rate keys deduplicate identically with archives produced on Windows.
pub fn canonical_utc_instant(dt: DateTime<FixedOffset>) -> String {
    let utc = dt.with_timezone(&Utc);
    let ticks_frac = utc.timestamp_subsec_nanos() / 100;
    format!("{}.{:07}Z", utc.format("%Y-%m-%dT%H:%M:%S"), ticks_frac)
}

fn canonical_heart_rate_key(row: &WorkoutHeartRate) -> String {
    let instant = canonical_utc_instant(row.time);
    if row.is_aggregate {
        format!("bucket:{}:{instant}", row.info.source)
    } else {
        format!("sample:{}:{instant}", row.info.id)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutRecordInfo {
    pub id: String,
    pub source: String,
    pub modified_at: DateTime<FixedOffset>,
    #[serde(default)]
    pub recording_method: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutSession {
    pub info: WorkoutRecordInfo,
    pub start: DateTime<FixedOffset>,
    pub end: DateTime<FixedOffset>,
    pub duration_seconds: i32,
    #[serde(default, rename = "type")]
    pub r#type: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutHeartRate {
    pub info: WorkoutRecordInfo,
    pub time: DateTime<FixedOffset>,
    pub bpm: f64,
    #[serde(default)]
    pub minimum: Option<f64>,
    #[serde(default)]
    pub maximum: Option<f64>,
    pub is_aggregate: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutCalories {
    pub info: WorkoutRecordInfo,
    pub start: DateTime<FixedOffset>,
    pub end: DateTime<FixedOffset>,
    pub kilocalories: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutDeletion {
    #[serde(rename = "type")]
    pub r#type: String,
    pub id: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutDataBatch {
    pub uploaded_at: DateTime<FixedOffset>,
    #[serde(default)]
    pub sessions: Vec<WorkoutSession>,
    #[serde(default)]
    pub heart_rate: Vec<WorkoutHeartRate>,
    #[serde(default)]
    pub total_calories: Vec<WorkoutCalories>,
    #[serde(default)]
    pub deleted: Vec<WorkoutDeletion>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutDelivery {
    pub id: String,
    pub data: WorkoutDataBatch,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutAcknowledgement {
    pub ids: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct StoredHeartRate {
    pub reading: WorkoutHeartRate,
    pub uploaded_at: DateTime<FixedOffset>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutSensorLinks {
    #[serde(default)]
    pub heart_rate_keys: Vec<String>,
    #[serde(default)]
    pub total_calorie_ids: Vec<String>,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct WorkoutCreditTotals {
    pub weekday_seconds: i64,
    pub weekend_seconds: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct WorkoutArchive {
    #[serde(default)]
    pub sessions: BTreeMap<String, WorkoutSession>,
    #[serde(default)]
    pub heart_rate: BTreeMap<String, StoredHeartRate>,
    #[serde(default)]
    pub total_calories: BTreeMap<String, WorkoutCalories>,
    #[serde(default)]
    pub deletions: BTreeMap<String, DateTime<FixedOffset>>,
    #[serde(default)]
    pub sensor_links: BTreeMap<String, WorkoutSensorLinks>,
    #[serde(skip)]
    primary_corrupt: bool,
}

impl WorkoutArchive {
    pub fn retention() -> Duration {
        Duration::days(90)
    }

    /// Re-key loaded heart-rate entries to canonical 7-digit UTC keys so archives
    /// migrated from Windows or older Rust runs never duplicate entries.
    pub fn canonicalize_heart_rate_keys(&mut self) {
        let old = std::mem::take(&mut self.heart_rate);
        for (_old_key, entry) in old {
            let key = canonical_heart_rate_key(&entry.reading);
            let replace = self
                .heart_rate
                .get(&key)
                .map(|prev| entry.uploaded_at > prev.uploaded_at)
                .unwrap_or(true);
            if replace {
                self.heart_rate.insert(key, entry);
            }
        }
    }

    pub fn apply(&mut self, batch: &WorkoutDataBatch) {
        for row in &batch.sessions {
            if self.can_apply("exercise", &row.info)
                && self
                    .sessions
                    .get(&row.info.id)
                    .map(|prev| row.info.modified_at > prev.info.modified_at)
                    .unwrap_or(true)
            {
                self.sessions.insert(row.info.id.clone(), row.clone());
            }
        }

        for row in &batch.total_calories {
            if self.can_apply("total_calories", &row.info)
                && self
                    .total_calories
                    .get(&row.info.id)
                    .map(|prev| row.info.modified_at > prev.info.modified_at)
                    .unwrap_or(true)
            {
                self.total_calories.insert(row.info.id.clone(), row.clone());
            }
        }

        for row in &batch.heart_rate {
            if !self.can_apply("heart_rate", &row.info) {
                continue;
            }
            let key = canonical_heart_rate_key(row);
            let should_insert = self
                .heart_rate
                .get(&key)
                .map(|prev| batch.uploaded_at > prev.uploaded_at)
                .unwrap_or(true);
            if should_insert {
                self.heart_rate.insert(
                    key,
                    StoredHeartRate {
                        reading: row.clone(),
                        uploaded_at: batch.uploaded_at,
                    },
                );
            }
        }

        let mut heart_rate_deletes = HashSet::new();
        for row in &batch.deleted {
            let key = format!("{}:{}", row.r#type, row.id);
            if let Some(prior) = self.deletions.get(&key)
                && *prior >= batch.uploaded_at
            {
                continue;
            }
            self.deletions.insert(key, batch.uploaded_at);
            if row.r#type == "exercise"
                && let Some(workout) = self.sessions.get(&row.id)
                && !self.can_apply(&row.r#type, &workout.info)
            {
                self.sessions.remove(&row.id);
            }
            if row.r#type == "total_calories"
                && let Some(cal) = self.total_calories.get(&row.id)
                && !self.can_apply(&row.r#type, &cal.info)
            {
                self.total_calories.remove(&row.id);
            }
            if row.r#type == "heart_rate" {
                heart_rate_deletes.insert(row.id.clone());
            }
        }

        if !heart_rate_deletes.is_empty() {
            let to_remove: Vec<String> = self
                .heart_rate
                .iter()
                .filter(|(_, entry)| {
                    heart_rate_deletes.contains(&entry.reading.info.id)
                        && !self.can_apply("heart_rate", &entry.reading.info)
                })
                .map(|(k, _)| k.clone())
                .collect();
            for key in to_remove {
                self.heart_rate.remove(&key);
            }
        }
    }

    fn can_apply(&self, record_type: &str, info: &WorkoutRecordInfo) -> bool {
        let key = format!("{record_type}:{}", info.id);
        match self.deletions.get(&key) {
            Some(deleted_at) => info.modified_at > *deleted_at,
            None => true,
        }
    }

    pub fn link_sensors(&mut self) {
        let mut cal_by_key: HashMap<(String, DateTime<Utc>, DateTime<Utc>), Vec<String>> =
            HashMap::new();
        for row in self.total_calories.values() {
            cal_by_key
                .entry((
                    row.info.source.clone(),
                    row.start.with_timezone(&Utc),
                    row.end.with_timezone(&Utc),
                ))
                .or_default()
                .push(row.info.id.clone());
        }

        let mut readings: Vec<(&String, &StoredHeartRate)> = self.heart_rate.iter().collect();
        readings.sort_by_key(|(_, entry)| entry.reading.time.with_timezone(&Utc));

        let mut links = BTreeMap::new();
        for (id, session) in &self.sessions {
            let start_utc = session.start.with_timezone(&Utc);
            let end_utc = session.end.with_timezone(&Utc);
            let earliest = start_utc - Duration::minutes(1);

            let idx = readings.partition_point(|(_, entry)| {
                entry.reading.time.with_timezone(&Utc) < earliest
            });

            let mut matching = Vec::new();
            for (key, entry) in &readings[idx..] {
                let t = entry.reading.time.with_timezone(&Utc);
                if t >= end_utc {
                    break;
                }
                if entry.reading.info.source == session.info.source
                    && if entry.reading.is_aggregate {
                        t > earliest
                    } else {
                        t >= start_utc
                    }
                {
                    matching.push((*key).clone());
                }
            }

            let cal_ids = cal_by_key
                .get(&(session.info.source.clone(), start_utc, end_utc))
                .cloned()
                .unwrap_or_default();

            links.insert(
                id.clone(),
                WorkoutSensorLinks {
                    heart_rate_keys: matching,
                    total_calorie_ids: cal_ids,
                },
            );
        }

        self.sensor_links = links;
    }

    pub fn prune(&mut self, now_utc: DateTime<Utc>) -> bool {
        let cutoff = now_utc - Self::retention();
        let before = self.sessions.len()
            + self.total_calories.len()
            + self.heart_rate.len()
            + self.deletions.len();

        self.sessions
            .retain(|_, row| row.end.with_timezone(&Utc) >= cutoff);
        self.total_calories
            .retain(|_, row| row.end.with_timezone(&Utc) >= cutoff);
        self.heart_rate
            .retain(|_, entry| entry.reading.time.with_timezone(&Utc) >= cutoff);
        self.deletions
            .retain(|_, deleted_at| deleted_at.with_timezone(&Utc) >= cutoff);

        let after = self.sessions.len()
            + self.total_calories.len()
            + self.heart_rate.len()
            + self.deletions.len();
        after < before
    }

    pub fn load(path: &Path) -> Self {
        let mut bak = path.as_os_str().to_os_string();
        bak.push(".bak");
        let bak_path = PathBuf::from(bak);

        let mut unreadable = Vec::new();
        for (candidate, is_backup) in [(path.to_path_buf(), false), (bak_path, true)] {
            if !candidate.exists() {
                continue;
            }
            match fs::read_to_string(&candidate)
                .ok()
                .and_then(|s| serde_json::from_str::<WorkoutArchive>(&s).ok())
            {
                Some(mut archive) => {
                    archive.canonicalize_heart_rate_keys();
                    archive.primary_corrupt = is_backup;
                    if !unreadable.is_empty() {
                        warn!(
                            "Workout archive {} was unreadable; recovered its backup.",
                            path.display()
                        );
                    }
                    return archive;
                }
                None => {
                    warn!("Workout archive file {} is unreadable.", candidate.display());
                    unreadable.push(candidate);
                }
            }
        }

        if !unreadable.is_empty() {
            let stamp = Utc::now().format("%Y%m%d%H%M%S");
            for damaged in unreadable {
                let mut target = damaged.as_os_str().to_os_string();
                target.push(format!(".corrupt-{stamp}"));
                let _ = fs::rename(&damaged, PathBuf::from(target));
            }
            error!(
                "Workout archive and backup are unreadable; moved them aside and started an empty archive."
            );
        }
        Self::default()
    }

    pub fn save(&mut self, path: &Path) -> std::io::Result<()> {
        let bytes = serde_json::to_vec_pretty(self)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        atomic_write_durable(path, &bytes, None, true, self.primary_corrupt)?;
        self.primary_corrupt = false;
        Ok(())
    }
}

pub struct WorkoutCredit;

impl WorkoutCredit {
    pub fn for_week(
        archive: &WorkoutArchive,
        week_start_logical_day: &str,
        today_logical_day: &str,
    ) -> WorkoutCreditTotals {
        let mut weekdays: Vec<&WorkoutSession> = Vec::new();
        let mut weekend: Vec<&WorkoutSession> = Vec::new();

        for session in archive.sessions.values() {
            if session.duration_seconds <= 0 || session.end <= session.start {
                continue;
            }
            let start_policy = PolicyClock::from_utc(session.start.with_timezone(&Utc));
            let day = LogicalDay::for_policy(start_policy);
            if day.as_str() < week_start_logical_day || day.as_str() > today_logical_day {
                continue;
            }
            if limits::is_weekend(start_policy.weekday()) {
                weekend.push(session);
            } else {
                weekdays.push(session);
            }
        }

        let weekday_workout_secs = Self::workout_seconds(&mut weekdays);
        let mut weekend_workout_secs = Self::workout_seconds(&mut weekend);

        if let (Some(max_weekday_end), Some(first_weekend)) = (
            weekdays.iter().map(|s| s.end.with_timezone(&Utc)).max(),
            weekend.first(),
        ) && max_weekday_end > first_weekend.start.with_timezone(&Utc)
        {
            let mut combined: Vec<&WorkoutSession> =
                Vec::with_capacity(weekdays.len() + weekend.len());
            combined.extend_from_slice(&weekdays);
            combined.extend_from_slice(&weekend);
            let combined_secs = Self::workout_seconds(&mut combined);
            weekend_workout_secs =
                weekend_workout_secs.min(combined_secs.saturating_sub(weekday_workout_secs));
        }

        WorkoutCreditTotals {
            weekday_seconds: weekday_workout_secs * 33 / 10,
            weekend_seconds: weekend_workout_secs * 33 / 10,
        }
    }

    fn workout_seconds(sessions: &mut [&WorkoutSession]) -> i64 {
        sessions.sort_by_key(|s| s.start.with_timezone(&Utc));
        let mut total = 0i64;
        let mut i = 0;
        while i < sessions.len() {
            let group_start = sessions[i].start.with_timezone(&Utc);
            let mut group_end = sessions[i].end.with_timezone(&Utc);
            let mut reported = 0i64;
            while i < sessions.len() && sessions[i].start.with_timezone(&Utc) < group_end {
                let s_end = sessions[i].end.with_timezone(&Utc);
                if s_end > group_end {
                    group_end = s_end;
                }
                reported += i64::from(sessions[i].duration_seconds);
                i += 1;
            }
            let span_secs = (group_end - group_start).num_seconds().max(0);
            total += reported.min(span_secs);
        }
        total
    }
}

#[cfg(test)]
mod tests {
    use tempfile::TempDir;

    use super::*;

    fn session(id: &str, start: &str, end: &str, dur: i32) -> WorkoutSession {
        let start = DateTime::parse_from_rfc3339(start).unwrap();
        let end = DateTime::parse_from_rfc3339(end).unwrap();
        WorkoutSession {
            info: WorkoutRecordInfo {
                id: id.into(),
                source: SIMPLE_HEALTH_SOURCE.into(),
                modified_at: end,
                recording_method: Some("manual_entry".into()),
            },
            start,
            end,
            duration_seconds: dur,
            r#type: Some("0".into()),
            title: Some("Workout".into()),
        }
    }

    #[test]
    fn sample_612s_earns_2019s() {
        let mut archive = WorkoutArchive::default();
        archive.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T15:00:00-05:00").unwrap(),
            sessions: vec![session(
                "w1",
                "2026-11-23T14:00:00-05:00",
                "2026-11-23T14:10:12.184-05:00",
                612,
            )],
            heart_rate: vec![],
            total_calories: vec![],
            deleted: vec![],
        });
        let totals = WorkoutCredit::for_week(&archive, "2026-11-23", "2026-11-23");
        assert_eq!(totals.weekday_seconds, 2019);
        assert_eq!(totals.weekend_seconds, 0);
    }

    #[test]
    fn overlapping_sessions_count_shared_time_once() {
        let mut archive = WorkoutArchive::default();
        archive.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T16:00:00-05:00").unwrap(),
            sessions: vec![
                session(
                    "a",
                    "2026-11-23T14:00:00-05:00",
                    "2026-11-23T14:10:00-05:00",
                    600,
                ),
                session(
                    "b",
                    "2026-11-23T14:05:00-05:00",
                    "2026-11-23T14:15:00-05:00",
                    600,
                ),
            ],
            heart_rate: vec![],
            total_calories: vec![],
            deleted: vec![],
        });
        let totals = WorkoutCredit::for_week(&archive, "2026-11-23", "2026-11-23");
        assert_eq!(totals.weekday_seconds, 2970);
    }

    #[test]
    fn deletion_tombstone_blocks_older_reupload() {
        let mut archive = WorkoutArchive::default();
        let s = session(
            "w1",
            "2026-11-23T14:00:00-05:00",
            "2026-11-23T14:10:00-05:00",
            600,
        );
        archive.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T15:00:00-05:00").unwrap(),
            sessions: vec![s.clone()],
            heart_rate: vec![],
            total_calories: vec![],
            deleted: vec![],
        });
        archive.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T16:00:00-05:00").unwrap(),
            sessions: vec![],
            heart_rate: vec![],
            total_calories: vec![],
            deleted: vec![WorkoutDeletion {
                r#type: "exercise".into(),
                id: "w1".into(),
            }],
        });
        assert!(archive.sessions.is_empty());

        archive.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T17:00:00-05:00").unwrap(),
            sessions: vec![s],
            heart_rate: vec![],
            total_calories: vec![],
            deleted: vec![],
        });
        assert!(archive.sessions.is_empty());
    }

    #[test]
    fn windows_7digit_heart_rate_keys_deduplicate_cleanly() {
        let dir = TempDir::new().unwrap();
        let path = dir.path().join("workouts.json");
        // Windows .NET writes 7 fractional digits in heart-rate dictionary keys.
        let win_json = r#"{
            "sessions": {},
            "heartRate": {
                "bucket:com.mc.simplehealth:2026-11-23T19:00:00.0000000Z": {
                    "reading": {
                        "info": {
                            "id": "hr1",
                            "source": "com.mc.simplehealth",
                            "modifiedAt": "2026-11-23T19:01:00Z",
                            "recordingMethod": "actively_recorded"
                        },
                        "time": "2026-11-23T19:00:00Z",
                        "bpm": 135.0,
                        "minimum": 120.0,
                        "maximum": 145.0,
                        "isAggregate": true
                    },
                    "uploadedAt": "2026-11-23T19:05:00Z"
                }
            },
            "totalCalories": {},
            "deletions": {},
            "sensorLinks": {}
        }"#;
        fs::write(&path, win_json).unwrap();

        let mut loaded = WorkoutArchive::load(&path);
        assert_eq!(loaded.heart_rate.len(), 1);

        // Replaying a newer upload of the exact same reading must collapse into 1 key.
        loaded.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T19:10:00Z").unwrap(),
            sessions: vec![],
            heart_rate: vec![WorkoutHeartRate {
                info: WorkoutRecordInfo {
                    id: "hr1".into(),
                    source: SIMPLE_HEALTH_SOURCE.into(),
                    modified_at: DateTime::parse_from_rfc3339("2026-11-23T19:02:00Z").unwrap(),
                    recording_method: Some("actively_recorded".into()),
                },
                time: DateTime::parse_from_rfc3339("2026-11-23T19:00:00Z").unwrap(),
                bpm: 140.0,
                minimum: Some(120.0),
                maximum: Some(150.0),
                is_aggregate: true,
            }],
            total_calories: vec![],
            deleted: vec![],
        });
        assert_eq!(loaded.heart_rate.len(), 1);
        let entry = loaded.heart_rate.values().next().unwrap();
        assert_eq!(entry.reading.bpm, 140.0);
    }

    #[test]
    fn corrupt_primary_recovers_backup_and_preserves_backup_on_save() {
        let dir = TempDir::new().unwrap();
        let path = dir.path().join("workouts.json");
        let mut archive = WorkoutArchive::default();
        archive.apply(&WorkoutDataBatch {
            uploaded_at: DateTime::parse_from_rfc3339("2026-11-23T15:00:00-05:00").unwrap(),
            sessions: vec![session(
                "w1",
                "2026-11-23T14:00:00-05:00",
                "2026-11-23T14:10:00-05:00",
                600,
            )],
            heart_rate: vec![],
            total_calories: vec![],
            deleted: vec![],
        });
        archive.save(&path).unwrap();
        // Second save creates .bak
        archive.save(&path).unwrap();
        // Corrupt primary
        fs::write(&path, "{corrupt").unwrap();

        let mut recovered = WorkoutArchive::load(&path);
        assert!(recovered.sessions.contains_key("w1"));
        // Saving recovered archive must NOT overwrite .bak with `{corrupt`
        recovered.save(&path).unwrap();
        let bak_text = fs::read_to_string(dir.path().join("workouts.json.bak")).unwrap();
        assert!(serde_json::from_str::<WorkoutArchive>(&bak_text).is_ok());
    }
}
