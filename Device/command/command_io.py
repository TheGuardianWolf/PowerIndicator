class CommandIO(object):
    def __init__(self):
        self.commands = {}

    def add_command(self, command_type, command_name, callback):
        if command_type not in self.commands:
            self.commands[command_type] = {}
        self.commands[command_type][command_name] = callback

    def send_event(self, event):
        print("event,{}".format(event))

    def send_response(self, response):
        print("response,{}".format(response))

    def parse(self):
        text = input()
        command_parts = text.split(",")

        if len(command_parts) == 2:
            command_type, command_name = command_parts
            if (
                command_type in self.commands
                and command_name in self.commands[command_type]
            ):
                callback = self.commands[command_type][command_name]

                response, deferred, blocking = callback()

                self.send_response(response)

                if blocking:
                    self.send_event("REQUEST_BLOCKING")

                if deferred is not None:
                    deferred()

            else:
                self.send_response("REQUEST_UNRECOGNISED")
        else:
            self.send_response("REQUEST_ERROR")
