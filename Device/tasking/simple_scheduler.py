import time
from collections import OrderedDict


NS_PER_MS = 1000000
MS_PER_S = 0.001


class SimpleScheduler(object):
    def __init__(self, tick_ms=1):
        self.tasks = OrderedDict()
        self.tick_ms = tick_ms

    def add_task(self, task):
        self.tasks[task.name] = task

    def remove_task(self, task):
        del self.tasks[task.name]

    def run(self):
        while True:
            runtime = time.monotonic_ns() // NS_PER_MS

            for task in self.tasks.values():
                time_since_last_run = runtime - task.last_run_ms
                task_period_expired = time_since_last_run >= task.period_ms

                if task_period_expired:
                    task.last_run_ms = runtime
                    task.callback()
                    if task.oneshot:
                        self.remove_task(task.name)

            if self.tick_ms > 1:
                time.sleep(self.tick_ms * MS_PER_S)
