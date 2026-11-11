use mdc_audio::MmcssTask;

#[test]
fn exact_values_round_trip_and_task_names() {
    for (text, task, name) in [
        ("none", MmcssTask::None, None),
        ("audio", MmcssTask::Audio, Some("Audio")),
        ("pro-audio", MmcssTask::ProAudio, Some("Pro Audio")),
    ] {
        assert_eq!(MmcssTask::parse(text), Some(task));
        assert_eq!(task.as_str(), text);
        assert_eq!(MmcssTask::parse(task.as_str()), Some(task));
        assert_eq!(task.task_name(), name);
    }
}

#[test]
fn rejects_non_exact_values() {
    for value in [
        "",
        "Audio",
        "AUDIO",
        "None",
        "Pro-Audio",
        "Pro Audio",
        "pro_audio",
        "other",
        " none",
        "none ",
        " audio",
        "audio ",
        " pro-audio",
        "pro-audio ",
        "audio\n",
        "\taudio",
        "audio\0",
    ] {
        assert_eq!(MmcssTask::parse(value), None, "{value:?}");
    }
}
