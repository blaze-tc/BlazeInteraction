#include "blaze_hand_tracking.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

#include "mediapipe/tasks/c/vision/core/image.h"
#include "mediapipe/tasks/c/vision/hand_landmarker/hand_landmarker.h"

namespace {

struct HandState {
  MpHandLandmarkerPtr landmarker = nullptr;
  std::mutex mutex;
  std::string last_error;
  std::vector<BlazeHandResult> hands;
  int64_t last_timestamp_ms = -1;
};

thread_local std::string global_last_error;

HandState* AsState(BlazeHandHandle handle) {
  return static_cast<HandState*>(handle);
}

void StoreError(BlazeHandHandle handle, const std::string& message) noexcept {
  try {
    if (auto* state = AsState(handle); state != nullptr) {
      state->last_error = message;
    } else {
      global_last_error = message;
    }
  } catch (...) {
  }
}

std::string TakeMediaPipeError(char* error_message,
                               const char* fallback) {
  std::unique_ptr<char, decltype(&std::free)> owned_error(error_message,
                                                          &std::free);
  if (owned_error != nullptr && owned_error.get()[0] != '\0') {
    return std::string(owned_error.get());
  }
  return std::string(fallback);
}

void ThrowIfMediaPipeFailed(MpStatus status, char* error_message,
                            const char* fallback) {
  if (status != kMpOk) {
    throw std::runtime_error(TakeMediaPipeError(error_message, fallback));
  }
  std::free(error_message);
}

bool IsConfidence(float value) {
  return std::isfinite(value) && value >= 0.0f && value <= 1.0f;
}

template <typename Operation>
int32_t Guard(BlazeHandHandle handle, Operation&& operation) noexcept {
  try {
    operation();
    return BLAZE_HAND_SUCCESS;
  } catch (const std::invalid_argument& error) {
    StoreError(handle, error.what());
    return BLAZE_HAND_INVALID_ARGUMENT;
  } catch (const std::logic_error& error) {
    StoreError(handle, error.what());
    return BLAZE_HAND_INVALID_STATE;
  } catch (const std::exception& error) {
    StoreError(handle, error.what());
    return BLAZE_HAND_NATIVE_FAILURE;
  } catch (...) {
    StoreError(handle, "Unknown native failure.");
    return BLAZE_HAND_UNKNOWN_FAILURE;
  }
}

class ScopedImage final {
 public:
  ~ScopedImage() {
    if (image_ != nullptr) {
      MpImageFree(image_);
    }
  }

  MpImagePtr* Out() { return &image_; }
  MpImagePtr Get() const { return image_; }

 private:
  MpImagePtr image_ = nullptr;
};

class ScopedResult final {
 public:
  ~ScopedResult() { MpHandLandmarkerCloseResult(&result_); }

  HandLandmarkerResult* Get() { return &result_; }

 private:
  HandLandmarkerResult result_{};
};

}  // namespace

uint32_t BLAZE_HAND_CALL blaze_hand_get_abi_version(void) {
  return BLAZE_HAND_ABI_VERSION;
}

int32_t BLAZE_HAND_CALL blaze_hand_create(const BlazeHandOptions* options,
                                          BlazeHandHandle* out_handle) {
  return Guard(nullptr, [&] {
    if (out_handle == nullptr) {
      throw std::invalid_argument("out_handle must not be null.");
    }
    *out_handle = nullptr;
    if (options == nullptr || options->struct_size < sizeof(BlazeHandOptions)) {
      throw std::invalid_argument("BlazeHandOptions has an invalid size.");
    }
    if (options->max_hands <= 0) {
      throw std::invalid_argument("max_hands must be positive.");
    }
    if (!IsConfidence(options->min_detection_confidence) ||
        !IsConfidence(options->min_tracking_confidence)) {
      throw std::invalid_argument("Confidence values must be finite and between zero and one.");
    }
    if (options->model_path_utf8 == nullptr ||
        options->model_path_utf8[0] == '\0') {
      throw std::invalid_argument("model_path_utf8 must not be empty.");
    }

    auto state = std::make_unique<HandState>();
    HandLandmarkerOptions media_pipe_options{};
    media_pipe_options.base_options.model_asset_path = options->model_path_utf8;
    media_pipe_options.base_options.delegate = Delegate::CPU;
    media_pipe_options.base_options.host_environment =
        HostEnvironment::HOST_ENVIRONMENT_UNKNOWN;
    media_pipe_options.base_options.host_system = HostSystem::HOST_SYSTEM_WINDOWS;
    media_pipe_options.running_mode = RunningMode::VIDEO;
    media_pipe_options.num_hands = options->max_hands;
    media_pipe_options.min_hand_detection_confidence =
        options->min_detection_confidence;
    media_pipe_options.min_hand_presence_confidence =
        options->min_detection_confidence;
    media_pipe_options.min_tracking_confidence =
        options->min_tracking_confidence;

    char* error_message = nullptr;
    const MpStatus status = MpHandLandmarkerCreate(
        &media_pipe_options, &state->landmarker, &error_message);
    ThrowIfMediaPipeFailed(status, error_message,
                           "MediaPipe failed to create Hand Landmarker.");
    if (state->landmarker == nullptr) {
      throw std::runtime_error("MediaPipe returned a null Hand Landmarker.");
    }

    *out_handle = state.release();
  });
}

int32_t BLAZE_HAND_CALL blaze_hand_process_frame(
    BlazeHandHandle handle, const uint8_t* rgb_data, int32_t width,
    int32_t height, int32_t stride_bytes, int64_t timestamp_ms) {
  return Guard(handle, [&] {
    auto* state = AsState(handle);
    if (state == nullptr) {
      throw std::invalid_argument("handle must not be null.");
    }
    if (rgb_data == nullptr || width <= 0 || height <= 0) {
      throw std::invalid_argument("RGB frame pointer and dimensions are invalid.");
    }
    if (width > std::numeric_limits<int32_t>::max() / 3) {
      throw std::invalid_argument("RGB frame width is too large.");
    }
    const int32_t row_bytes = width * 3;
    if (stride_bytes < row_bytes) {
      throw std::invalid_argument("RGB frame stride is smaller than one row.");
    }
    if (height > std::numeric_limits<int32_t>::max() / row_bytes) {
      throw std::invalid_argument("RGB frame is too large.");
    }

    std::lock_guard<std::mutex> lock(state->mutex);
    if (timestamp_ms <= state->last_timestamp_ms) {
      throw std::logic_error("Frame timestamps must be strictly increasing.");
    }

    std::vector<uint8_t> contiguous;
    std::vector<uint8_t> square_frame;
    const uint8_t* input = rgb_data;
    const int32_t pixel_bytes = row_bytes * height;
    if (stride_bytes != row_bytes) {
      contiguous.resize(static_cast<size_t>(pixel_bytes));
      for (int32_t row = 0; row < height; ++row) {
        std::memcpy(contiguous.data() + static_cast<size_t>(row) * row_bytes,
                    rgb_data + static_cast<size_t>(row) * stride_bytes,
                    static_cast<size_t>(row_bytes));
      }
      input = contiguous.data();
    }

    // MediaPipe's Windows FrameBuffer converter does not implement the zero
    // border requested by the palm detector. Apply the equivalent letterbox
    // before entering the graph, then map landmarks back to the source frame.
    const int32_t square_side = std::max(width, height);
    if (square_side > std::numeric_limits<int32_t>::max() / 3) {
      throw std::invalid_argument("RGB frame dimensions are too large to letterbox.");
    }
    const int32_t square_row_bytes = square_side * 3;
    if (square_side >
        std::numeric_limits<int32_t>::max() / square_row_bytes) {
      throw std::invalid_argument("RGB frame is too large to letterbox.");
    }
    const int32_t square_pixel_bytes = square_side * square_row_bytes;
    const int32_t offset_x = (square_side - width) / 2;
    const int32_t offset_y = (square_side - height) / 2;
    if (width != height) {
      square_frame.assign(static_cast<size_t>(square_pixel_bytes), 0);
      for (int32_t row = 0; row < height; ++row) {
        std::memcpy(
            square_frame.data() +
                static_cast<size_t>(row + offset_y) * square_row_bytes +
                static_cast<size_t>(offset_x) * 3,
            input + static_cast<size_t>(row) * row_bytes,
            static_cast<size_t>(row_bytes));
      }
      input = square_frame.data();
    }

    ScopedImage image;
    char* error_message = nullptr;
    MpStatus status = MpImageCreateFromUint8Data(
        kMpImageFormatSrgb, square_side, square_side, input,
        width == height ? pixel_bytes : square_pixel_bytes, image.Out(),
        &error_message);
    ThrowIfMediaPipeFailed(status, error_message,
                           "MediaPipe failed to create an RGB image.");

    ScopedResult result;
    error_message = nullptr;
    status = MpHandLandmarkerDetectForVideo(
        state->landmarker, image.Get(), nullptr, timestamp_ms, result.Get(),
        &error_message);
    ThrowIfMediaPipeFailed(status, error_message,
                           "MediaPipe hand detection failed.");

    const HandLandmarkerResult& detected = *result.Get();
    std::vector<BlazeHandResult> snapshot;
    snapshot.reserve(detected.hand_landmarks_count);
    for (uint32_t hand_index = 0;
         hand_index < detected.hand_landmarks_count; ++hand_index) {
      const NormalizedLandmarks& landmarks =
          detected.hand_landmarks[hand_index];
      if (landmarks.landmarks == nullptr ||
          landmarks.landmarks_count != BLAZE_HAND_LANDMARK_COUNT) {
        throw std::runtime_error("MediaPipe returned a hand without exactly 21 landmarks.");
      }

      BlazeHandResult hand{};
      hand.confidence = 1.0f;
      if (detected.handedness != nullptr &&
          hand_index < detected.handedness_count &&
          detected.handedness[hand_index].categories != nullptr &&
          detected.handedness[hand_index].categories_count > 0) {
        hand.confidence =
            detected.handedness[hand_index].categories[0].score;
      }

      for (uint32_t landmark_index = 0;
           landmark_index < BLAZE_HAND_LANDMARK_COUNT; ++landmark_index) {
        const NormalizedLandmark& source =
            landmarks.landmarks[landmark_index];
        if (!std::isfinite(source.x) || !std::isfinite(source.y) ||
            !std::isfinite(source.z)) {
          throw std::runtime_error("MediaPipe returned a non-finite hand landmark.");
        }
        hand.landmarks[landmark_index] = {
            (source.x * square_side - offset_x) / width,
            (source.y * square_side - offset_y) / height,
            source.z * square_side / width};
      }
      snapshot.push_back(hand);
    }

    state->hands = std::move(snapshot);
    state->last_timestamp_ms = timestamp_ms;
    state->last_error.clear();
  });
}

int32_t BLAZE_HAND_CALL blaze_hand_get_hand_count(
    BlazeHandHandle handle, int32_t* out_hand_count) {
  return Guard(handle, [&] {
    auto* state = AsState(handle);
    if (state == nullptr || out_hand_count == nullptr) {
      throw std::invalid_argument("handle and out_hand_count must not be null.");
    }
    std::lock_guard<std::mutex> lock(state->mutex);
    if (state->hands.size() >
        static_cast<size_t>(std::numeric_limits<int32_t>::max())) {
      throw std::runtime_error("Native hand count exceeds the ABI limit.");
    }
    *out_hand_count = static_cast<int32_t>(state->hands.size());
  });
}

int32_t BLAZE_HAND_CALL blaze_hand_copy_hand(
    BlazeHandHandle handle, int32_t hand_index, BlazeHandResult* out_result,
    uint32_t result_size) {
  return Guard(handle, [&] {
    auto* state = AsState(handle);
    if (state == nullptr || out_result == nullptr) {
      throw std::invalid_argument("handle and out_result must not be null.");
    }
    if (result_size < sizeof(BlazeHandResult)) {
      throw std::invalid_argument("BlazeHandResult buffer is too small.");
    }
    std::lock_guard<std::mutex> lock(state->mutex);
    if (hand_index < 0 ||
        static_cast<size_t>(hand_index) >= state->hands.size()) {
      throw std::invalid_argument("hand_index is outside the current result.");
    }
    *out_result = state->hands[static_cast<size_t>(hand_index)];
  });
}

int32_t BLAZE_HAND_CALL blaze_hand_destroy(BlazeHandHandle handle) {
  if (handle == nullptr) {
    return BLAZE_HAND_SUCCESS;
  }

  try {
    std::unique_ptr<HandState> state(AsState(handle));
    char* error_message = nullptr;
    const MpStatus status = MpHandLandmarkerClose(state->landmarker,
                                                   &error_message);
    state->landmarker = nullptr;
    if (status != kMpOk) {
      global_last_error = TakeMediaPipeError(
          error_message, "MediaPipe failed to close Hand Landmarker.");
      return BLAZE_HAND_NATIVE_FAILURE;
    }
    std::free(error_message);
    return BLAZE_HAND_SUCCESS;
  } catch (const std::exception& error) {
    StoreError(nullptr, error.what());
    return BLAZE_HAND_NATIVE_FAILURE;
  } catch (...) {
    StoreError(nullptr, "Unknown native failure.");
    return BLAZE_HAND_UNKNOWN_FAILURE;
  }
}

int32_t BLAZE_HAND_CALL blaze_hand_get_last_error(
    BlazeHandHandle handle, char* utf8_buffer, uint32_t buffer_capacity,
    uint32_t* out_required_size) {
  return Guard(handle, [&] {
    const std::string& message =
        handle == nullptr ? global_last_error : AsState(handle)->last_error;
    if (message.size() >= std::numeric_limits<uint32_t>::max()) {
      throw std::runtime_error("Native error text exceeds the ABI limit.");
    }
    const uint32_t required_size =
        static_cast<uint32_t>(message.size() + 1);
    if (out_required_size != nullptr) {
      *out_required_size = required_size;
    }
    if (utf8_buffer == nullptr && buffer_capacity == 0) {
      return;
    }
    if (utf8_buffer == nullptr || buffer_capacity == 0) {
      throw std::invalid_argument("Error buffer and capacity are inconsistent.");
    }

    const size_t copy_size =
        std::min(message.size(), static_cast<size_t>(buffer_capacity - 1));
    std::memcpy(utf8_buffer, message.data(), copy_size);
    utf8_buffer[copy_size] = '\0';
    if (buffer_capacity < required_size) {
      throw std::invalid_argument("Error buffer is too small.");
    }
  });
}
