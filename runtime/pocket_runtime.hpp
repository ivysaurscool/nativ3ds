#pragma once
#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>

namespace pocket {

// Minimal ABI surface for generated blocks. The production runtime will provide virtual memory,
// service shims, scheduling, audio, input, and graphics implementations behind this interface.
struct Cpu {
    std::array<std::uint32_t, 16> r{};
    std::uint32_t cpsr{};

    bool condition(std::uint32_t condition_code) const;
    void compare(std::uint32_t lhs, std::uint32_t rhs);
    std::uint32_t load32(std::uint32_t guest_address);
    void store32(std::uint32_t guest_address, std::uint32_t value);
    void jump(std::uint32_t guest_address);
    void call(std::uint32_t guest_address);
    void svc(std::uint32_t service_id);
    [[noreturn]] void unsupported(std::uint32_t instruction) {
        throw std::runtime_error("unsupported ARM instruction: " + std::to_string(instruction));
    }
};

struct RenderConfig {
    std::uint32_t output_width = 1920;
    std::uint32_t output_height = 1080;
    enum class Filter { nearest, linear, bicubic, fsr } filter = Filter::fsr;
};

} // namespace pocket
