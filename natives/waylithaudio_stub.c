/* Stub for the game's native audio library (waylithaudio / miniaudio wrapper).
 * The headless engine path only needs the symbols to resolve: -Headless initialises
 * the audio backend with nullBackend=1, so nothing here produces sound. */
#include <stdint.h>
#include <stddef.h>


long wa_channels() { return 2; }
long wa_group_create() { return 1; }
long wa_group_release() { return 0; }
long wa_group_set_mute() { return 0; }
long wa_group_set_paused() { return 0; }
long wa_group_set_pitch() { return 0; }
long wa_group_set_volume() { return 0; }
long wa_init() { return 0; }
long wa_last_error() { return 0; }
long wa_period_frames() { return 480; }
long wa_playing_count() { return 0; }
long wa_probe_file() { return 0; }
long wa_probe_memory() { return 0; }
long wa_sample_rate() { return 48000; }
long wa_set_allocator() { return 0; }
long wa_set_listener() { return 0; }
long wa_set_master_paused() { return 0; }
long wa_set_master_volume() { return 0; }
long wa_shutdown() { return 0; }
long wa_sound_channels() { return 2; }
long wa_sound_create_user() { return 1; }
long wa_sound_kind() { return 0; }
long wa_sound_length_frames() { return 0; }
long wa_sound_load_file_stream() { return 1; }
long wa_sound_load_memory() { return 1; }
long wa_sound_load_memory_stream() { return 1; }
long wa_sound_load_pcm() { return 1; }
long wa_sound_pcm() { return 0; }
long wa_sound_rate() { return 48000; }
long wa_sound_release() { return 0; }
long wa_sound_resident_bytes() { return 0; }
long wa_time_frames() { return 0; }
long wa_update() { return 0; }
long wa_voice_get_position_frames() { return 0; }
long wa_voice_is_playing() { return 0; }
long wa_voice_play() { return 1; }
long wa_voice_set_3d() { return 0; }
long wa_voice_set_3d_position() { return 0; }
long wa_voice_set_doppler() { return 0; }
long wa_voice_set_echo() { return 0; }
long wa_voice_set_group() { return 0; }
long wa_voice_set_highpass() { return 0; }
long wa_voice_set_loop() { return 0; }
long wa_voice_set_lowpass() { return 0; }
long wa_voice_set_mix_matrix() { return 0; }
long wa_voice_set_mute() { return 0; }
long wa_voice_set_pan() { return 0; }
long wa_voice_set_paused() { return 0; }
long wa_voice_set_pitch() { return 0; }
long wa_voice_set_position_frames() { return 0; }
long wa_voice_set_priority() { return 0; }
long wa_voice_set_reverb() { return 0; }
long wa_voice_set_start_time() { return 0; }
long wa_voice_set_volume() { return 0; }
long wa_voice_stop() { return 0; }
long wa_voice_tap_enable() { return 0; }
long wa_voice_tap_read() { return 0; }
