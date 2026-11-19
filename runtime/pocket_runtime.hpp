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
    std::uint32_t shift_immediate(std::uint32_t value, std::uint32_t shift_type,
                                  std::uint32_t amount, bool update_carry);
    void logical_flags(std::uint32_t value);
    void logical_flags(std::uint32_t value, bool carry);
    std::uint32_t add(std::uint32_t lhs, std::uint32_t rhs, bool set_flags);
    std::uint32_t subtract(std::uint32_t lhs, std::uint32_t rhs, bool set_flags);
    std::uint32_t load32(std::uint32_t guest_address);
    void store32(std::uint32_t guest_address, std::uint32_t value);
    std::uint8_t load8(std::uint32_t guest_address);
    void store8(std::uint32_t guest_address, std::uint8_t value);
    std::uint16_t load16(std::uint32_t guest_address);
    void store16(std::uint32_t guest_address, std::uint16_t value);
    std::uint32_t load_signed8(std::uint32_t guest_address);
    std::uint32_t load_signed16(std::uint32_t guest_address);
    std::uint32_t thread_pointer() const;
    bool load_multiple(std::uint32_t base_register, std::uint16_t register_list,
                       bool pre_index, bool increment, bool write_back);
    void store_multiple(std::uint32_t base_register, std::uint16_t register_list,
                        bool pre_index, bool increment, bool write_back);
    void push(std::uint16_t register_list);
    bool pop(std::uint16_t register_list);
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
