#ifndef TRANSLATION_MOSS_REVIEW_PROCESS_H_
#define TRANSLATION_MOSS_REVIEW_PROCESS_H_

#ifdef _WIN32
#include <stdint.h>
#else
#include <signal.h>
#include <spawn.h>
#include <sys/wait.h>
#include <unistd.h>
extern char **environ;
#endif

typedef struct {
  FILE *input, *output;
#ifdef _WIN32
  HANDLE process;
#else
  pid_t process;
#endif
} MossProcess;

static void moss_process_close(MossProcess *worker) {
  if (worker->input) fclose(worker->input);
  if (worker->output) fclose(worker->output);
  if (worker->process) {
#ifdef _WIN32
    WaitForSingleObject(worker->process, INFINITE);
    CloseHandle(worker->process);
#else
    while (waitpid(worker->process, NULL, 0) < 0 && errno == EINTR) {}
#endif
  }
  *worker = (MossProcess){0};
}

#ifdef _WIN32
static FILE *moss_pipe_file(HANDLE *pipe, int flags, const char *mode) {
  int fd = _open_osfhandle((intptr_t)*pipe, flags | _O_BINARY);
  if (fd < 0) return NULL;
  *pipe = NULL;
  FILE *file = _fdopen(fd, mode);
  if (!file) _close(fd);
  return file;
}
#endif

static int moss_process_start(MossProcess *worker, const char *program, const char *sherpa) {
#ifdef _WIN32
  (void)program;
  (void)sherpa;
  SECURITY_ATTRIBUTES security = {sizeof(security), NULL, TRUE};
  HANDLE to_child = NULL, child_input = NULL, child_output = NULL, from_child = NULL;
  STARTUPINFOW startup = {0};
  PROCESS_INFORMATION process = {0};
  wchar_t executable[32768], command[] = L"streaming-asr.exe --moss-worker -";
  DWORD length = GetModuleFileNameW(NULL, executable, sizeof(executable) / sizeof(*executable));
  if (!length || length >= sizeof(executable) / sizeof(*executable) ||
      !CreatePipe(&child_input, &to_child, &security, 0) ||
      !CreatePipe(&from_child, &child_output, &security, 0) ||
      !SetHandleInformation(to_child, HANDLE_FLAG_INHERIT, 0) ||
      !SetHandleInformation(from_child, HANDLE_FLAG_INHERIT, 0)) goto done;
  startup.cb = sizeof(startup);
  startup.dwFlags = STARTF_USESTDHANDLES;
  startup.hStdInput = child_input;
  startup.hStdOutput = child_output;
  startup.hStdError = GetStdHandle(STD_ERROR_HANDLE);
  if (!CreateProcessW(executable, command, NULL, NULL, TRUE, CREATE_NO_WINDOW,
                      NULL, NULL, &startup, &process)) goto done;
  CloseHandle(process.hThread);
  worker->process = process.hProcess;
  worker->input = moss_pipe_file(&to_child, _O_WRONLY, "wb");
  worker->output = moss_pipe_file(&from_child, _O_RDONLY, "rb");
done:
  if (child_input) CloseHandle(child_input);
  if (child_output) CloseHandle(child_output);
  if (to_child) CloseHandle(to_child);
  if (from_child) CloseHandle(from_child);
#else
  int input[2], output[2];
  if (pipe(input)) return 0;
  if (pipe(output)) { close(input[0]); close(input[1]); return 0; }
  posix_spawn_file_actions_t actions;
  if (!posix_spawn_file_actions_init(&actions)) {
    int error = posix_spawn_file_actions_adddup2(&actions, input[0], STDIN_FILENO) ||
                posix_spawn_file_actions_adddup2(&actions, output[1], STDOUT_FILENO) ||
                posix_spawn_file_actions_addclose(&actions, input[0]) ||
                posix_spawn_file_actions_addclose(&actions, input[1]) ||
                posix_spawn_file_actions_addclose(&actions, output[0]) ||
                posix_spawn_file_actions_addclose(&actions, output[1]);
    char *const arguments[] = {(char *)program, "--moss-worker", (char *)sherpa, NULL};
    pid_t pid;
    if (!error && !posix_spawnp(&pid, program, &actions, NULL, arguments, environ)) worker->process = pid;
    posix_spawn_file_actions_destroy(&actions);
  }
  close(input[0]);
  close(output[1]);
  worker->input = fdopen(input[1], "wb");
  if (!worker->input) close(input[1]);
  worker->output = fdopen(output[0], "rb");
  if (!worker->output) close(output[0]);
#endif
  unsigned char ready;
  if (worker->process && worker->input && worker->output &&
      fread(&ready, sizeof(ready), 1, worker->output) == 1 && ready == 1) return 1;
  moss_process_close(worker);
  return 0;
}

#endif
