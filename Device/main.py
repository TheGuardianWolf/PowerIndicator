import board
import time
from digitalio import DigitalInOut, Direction
from tasking import SimpleScheduler, SimpleTask
from command import CommandIO


# Pin configurations
power = DigitalInOut(board.PA09)
power.direction = Direction.INPUT

led = DigitalInOut(board.LED)
led.direction = Direction.OUTPUT
led.value = False


# Values
power_state = True
command_io = CommandIO()


# Task Callbacks
def blink_callback():
    led.value = not led.value


def power_monitor_callback():
    global power_state
    last_power_state = power_state
    power_state = power.value

    if last_power_state != power_state:
        if power_state:
            led.value = False
            command_io.send_event("POWER_RESTORED")
        else:
            led.value = True
            command_io.send_event("POWER_LOST")


# Command Callbacks
def command_blink_callback():
    def deferred():
        blink_task = SimpleTask("blink", blink_callback, period_ms=1000)
        scheduler = SimpleScheduler(200)
        scheduler.add_task(blink_task)
        scheduler.run()

    return "BLINK_ON", deferred, True


def command_power_state_callback():
    return "POWER_ON" if power.value else "POWER_OFF", None, False


def command_power_monitor_callback():
    def deferred():
        power_monitor_task = SimpleTask(
            "power_monitor", power_monitor_callback, period_ms=1000
        )
        scheduler = SimpleScheduler(200)
        scheduler.add_task(power_monitor_task)
        scheduler.run()

    return "POWER_MONITOR_ON", deferred, True


# Startup
command_io.add_command("request", "BLINK", command_blink_callback)
command_io.add_command("request", "POWER_STATE", command_power_state_callback)
command_io.add_command("request", "POWER_MONITOR", command_power_monitor_callback)
print("event,START")

# Input driven loop
while True:
    command_io.parse()
