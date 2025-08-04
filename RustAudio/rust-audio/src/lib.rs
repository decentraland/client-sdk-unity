use anyhow::{Context, Result, anyhow};
use cpal::{
    InputCallbackInfo, Stream, StreamConfig,
    traits::{DeviceTrait, HostTrait, StreamTrait},
};
use lazy_static::lazy_static;
use std::{
    collections::HashMap,
    ffi::{CStr, CString},
    os::raw::{c_char, c_void},
    ptr,
    sync::{
        Mutex,
        atomic::{AtomicU64, Ordering},
    },
};

lazy_static! {
    static ref AUDIO_CALLBACK: Mutex<AudioCallback> = Mutex::new(audio_callback_mock);
    static ref ERROR_CALLBACK: Mutex<ErrorCallback> = Mutex::new(error_callback_mock);
    static ref REGISTRY: Mutex<HashMap<StreamId, Stream>> = Mutex::new(HashMap::new());
    static ref NEXT_STREAM_ID: AtomicU64 = AtomicU64::new(1);
}

extern "C" fn audio_callback_mock(_: *const f32, _: i32) {}
extern "C" fn error_callback_mock(_: *const c_char) {}

pub type AudioCallback = extern "C" fn(*const f32, i32);

pub type ErrorCallback = extern "C" fn(*const c_char);

pub type StreamId = u64;

#[repr(C)]
pub struct DeviceNamesResult {
    names: *const *const c_char,
    length: i32,
    error_message: *const c_char,
}

#[repr(C)]
pub struct InputStreamResult {
    pub stream_id: StreamId,
    pub sample_rate: u32,
    pub channels: u32,
    pub error_message: *const c_char,
}

impl InputStreamResult {
    fn ok(stream_id: StreamId, sample_rate: u32, channels: u32) -> Self {
        Self {
            stream_id,
            sample_rate,
            channels,
            error_message: ptr::null(),
        }
    }

    fn error(message: &str) -> Self {
        Self {
            stream_id: 0,
            sample_rate: 0,
            channels: 0,
            error_message: string_to_c_bytes(message),
        }
    }
}

#[repr(C)]
pub struct ResultFFI {
    pub error_message: *const c_char,
}

impl ResultFFI {
    fn ok() -> Self {
        Self {
            error_message: ptr::null(),
        }
    }

    fn error(message: &str) -> Self {
        Self {
            error_message: string_to_c_bytes(message),
        }
    }

    fn from_anyhow<T>(result: Result<T>) -> Self {
        match result {
            Ok(_) => Self::ok(),
            Err(e) => Self::error(e.to_string().as_str()),
        }
    }
}

fn string_to_c_bytes(s: &str) -> *const c_char {
    CString::new(s).unwrap_or_default().into_raw()
}

fn vec_to_c_array(strings: Vec<String>) -> *const *const c_char {
    let cstrings: Vec<*const c_char> = strings.into_iter().map(|s| string_to_c_bytes(&s)).collect();

    let boxed_slice = cstrings.into_boxed_slice();

    let ptr = boxed_slice.as_ptr();
    std::mem::forget(boxed_slice);

    ptr
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_init(
    audio_callback: Option<AudioCallback>,
    error_callback: Option<ErrorCallback>,
) -> ResultFFI {
    let audio_callback = match audio_callback {
        Some(a) => a,
        None => {
            return ResultFFI::error("Audio callback is null");
        }
    };

    let error_callback = match error_callback {
        Some(e) => e,
        None => {
            return ResultFFI::error("Error callback is null");
        }
    };

    *AUDIO_CALLBACK.lock().unwrap() = audio_callback;
    *ERROR_CALLBACK.lock().unwrap() = error_callback;

    ResultFFI::ok()
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_deinit() {
    *AUDIO_CALLBACK.lock().unwrap() = audio_callback_mock;
    *ERROR_CALLBACK.lock().unwrap() = error_callback_mock;
    REGISTRY.lock().unwrap().clear();
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_free_c_char_array(ptr: *const *const c_char, len: usize) {
    unsafe {
        if ptr.is_null() {
            return;
        }

        let slice = std::slice::from_raw_parts(ptr, len);

        for &cstr_ptr in slice {
            if !cstr_ptr.is_null() {
                drop(CString::from_raw(cstr_ptr as *mut c_char));
            }
        }

        drop(Box::from_raw(ptr as *mut *const c_char));
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_free(ptr: *mut c_void) {
    if !ptr.is_null() {
        unsafe { drop(Box::from_raw(ptr)) };
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_input_device_names() -> DeviceNamesResult {
    let result = rust_audio_input_device_names_internal();
    match result {
        Ok(names) => DeviceNamesResult {
            error_message: ptr::null(),
            length: names.len() as i32,
            names: vec_to_c_array(names),
        },
        Err(e) => DeviceNamesResult {
            names: ptr::null(),
            length: 0,
            error_message: string_to_c_bytes(&e.to_string()),
        },
    }
}

fn rust_audio_input_device_names_internal() -> Result<Vec<String>> {
    let host = cpal::default_host();
    let devices: Vec<_> = host
        .input_devices()
        .context("cannot get input devices")?
        .collect();
    let mut names: Vec<String> = Vec::new();
    for d in devices {
        let name = d.name().context("cannot get device name")?;
        names.push(name);
    }
    Ok(names)
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_input_stream_new(device_name: *const c_char) -> InputStreamResult {
    {
        if device_name.is_null() {
            return InputStreamResult::error("Device name is null");
        }

        unsafe {
            let cstr = CStr::from_ptr(device_name);
            let device_name = match cstr.to_str() {
                Ok(name) => name,
                Err(_) => {
                    return InputStreamResult::error("Invalid UTF-8 in device name");
                }
            };

            match rust_audio_input_stream_new_internal(device_name) {
                Ok(s) => {
                    let stream = s.0;
                    let config = s.1;

                    let mut guard = REGISTRY.lock().unwrap();
                    let next_id = NEXT_STREAM_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                    guard.insert(next_id, stream);

                    InputStreamResult::ok(next_id, config.sample_rate.0, config.channels as u32)
                }
                Err(e) => {
                    let message = e.to_string();
                    InputStreamResult::error(message.as_str())
                }
            }
        }
    }
}

fn rust_audio_input_stream_new_internal(device_name: &str) -> Result<(Stream, StreamConfig)> {
    let host = cpal::default_host();
    let device = host
        .input_devices()
        .context("cannot get input devices")?
        .find(|d| d.name().unwrap_or_default() == device_name)
        .ok_or(anyhow!("device with specified name not found"))?;

    let config_range = device
        .supported_input_configs()
        .context("device doesn't have supported configs")?
        .find(|c| c.sample_format() == cpal::SampleFormat::F32)
        .ok_or(anyhow!("device doesn't support f32 samples"))?;

    let config = config_range.with_max_sample_rate().config();

    let stream = device
        .build_input_stream(
            &config,
            |data: &[f32], _: &InputCallbackInfo| {
                AUDIO_CALLBACK.lock().unwrap()(data.as_ptr(), data.len() as i32);
            },
            |e| {
                let content = e.to_string();
                let c_content = CString::new(content).unwrap_or_default();
                ERROR_CALLBACK.lock().unwrap()(c_content.as_ptr());
            },
            None,
        )
        .context("cannot build input stream")?;
    Ok((stream, config))
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_input_stream_start(stream_id: StreamId) -> ResultFFI {
    let guard = REGISTRY.lock().unwrap();
    let stream = guard.get(&stream_id);
    match stream {
        Some(s) => {
            let result = s.play().context("cannot play stream");
            ResultFFI::from_anyhow(result)
        }
        None => ResultFFI::error("stream with specified id not found"),
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn rust_audio_input_stream_pause(stream_id: StreamId) -> ResultFFI {
    let guard = REGISTRY.lock().unwrap();
    let stream = guard.get(&stream_id);
    match stream {
        Some(s) => {
            let result = s.pause().context("cannot play stream");
            ResultFFI::from_anyhow(result)
        }
        None => ResultFFI::error("stream with specified id not found"),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn rust_audio_input_stream_free(stream_id: StreamId) {
    let mut guard = REGISTRY.lock().unwrap();
    guard.remove(&stream_id);
}
