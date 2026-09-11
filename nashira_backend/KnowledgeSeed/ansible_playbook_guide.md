# Ansible Playbook Best Practices for AWX

## Critical YAML Rules for Playbooks

### Backslash Escaping in Regex (MOST COMMON ERROR)
YAML interprets backslashes as escape characters. This causes `found unknown escape character` errors.

**WRONG - Will fail in AWX:**
```yaml
- name: Extract version
  set_fact:
    version: "{{ output.stdout | regex_search('Software version\s*:\s*([\w\.-]+)', '\1') }}"
```

**CORRECT - Double escape backslashes:**
```yaml
- name: Extract version
  set_fact:
    version: >-
      {{ (output.stdout | regex_search('Software version\\s*:\\s*([\\w\\.-]+)', '\\1')) | default(['unknown']) | first }}
```

**Rules for regex in YAML:**
- `\s` must be `\\s` (whitespace)
- `\w` must be `\\w` (word character)
- `\d` must be `\\d` (digit)
- `\.` must be `\\.` (literal dot)
- `\1` backreference must be `\\1`
- Use `>-` (folded block scalar) for long Jinja2 expressions to avoid inline escaping issues
- `regex_search` with a capture group returns a list, always use `| first` to get the value

### Jinja2 Curly Braces in extra_vars
When passing playbook content as `extra_vars` in AWX, Jinja2 double braces conflict with YAML/JSON parsing.

**WRONG:**
```yaml
extra_vars:
  template: "{{ inventory_hostname }}"
```

**CORRECT - Use raw blocks or quadruple braces:**
```yaml
# Option 1: Quadruple braces in AWX extra_vars string
extra_vars: "template: '{{{{ inventory_hostname }}}}'"

# Option 2: In playbook file, use {% raw %} blocks
{% raw %}
  msg: "{{ inventory_hostname }}"
{% endraw %}
```

### Quoting Rules
```yaml
# Strings with special characters MUST be quoted
msg: "Status: {{ result }}"     # CORRECT
msg: Status: {{ result }}        # WRONG - colon breaks YAML

# Strings starting with * { } [ ] , ! | > must be quoted
name: "{{ my_var }}"             # CORRECT
name: {{ my_var }}               # WRONG
```

---

## Nokia SR Linux SSH Behavior (CRITICAL)

When you SSH into a Nokia SR Linux device as `admin`, you land **directly in the SR Linux CLI**, NOT in a Linux bash shell.

**This means:**
- `sr_cli` is NOT a valid command — it is a bash utility, not a CLI command
- You must send CLI commands directly: `show version`, `info system`, etc.
- The available commands are: `show`, `info`, `enter`, `set`, `tools`, `bash`, `ping`, `traceroute`, etc.

**WRONG — will fail with `Unknown token 'sr_cli'`:**
```yaml
- ansible.builtin.raw: "show version"
```

**CORRECT — send commands directly to the SR Linux CLI:**
```yaml
- ansible.builtin.raw: "show version"
```

**When to use `sr_cli`:**
- ONLY when the SSH session lands in a bash shell (e.g., connecting as `linuxadmin` user)
- In that case: `sr_cli 'show version'` works because you are in bash calling the CLI utility

**Summary:**
| SSH User | Shell | Command Format |
|----------|-------|---------------|
| `admin` | SR Linux CLI | `show version` (direct) |
| `linuxadmin` | bash | `sr_cli 'show version'` (via utility) |

---

## Connection Types for Network Devices

### When to use each connection type

| Connection | Use Case | Requires Collections |
|-----------|----------|---------------------|
| `ssh` + `raw` | Basic command execution, no collections needed | None |
| `network_cli` | Full network module support | `ansible.netcommon` + vendor collection |
| `httpapi` | REST API devices (e.g., SR Linux JSON-RPC) | Vendor collection |
| `local` | Running commands from the control node | None |

### Default AWX Execution Environments
The default AWX EE (`quay.io/ansible/awx-ee`) includes:
- `ansible.builtin` modules (raw, command, shell, debug, set_fact, set_stats, etc.)
- `ssh` connection plugin
- Does NOT include: `network_cli`, `ansible.netcommon`, vendor collections

**If you get `connection plugin 'network_cli' was not found`:**
- Switch to `connection: ssh` with `ansible.builtin.raw`
- Or create a custom Execution Environment with required collections

---

## Playbook Templates for Network Devices

### Template 1: Basic SSH Command (Works on any EE)
```yaml
---
- name: Run command via SSH
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  tasks:
    - name: Execute command
      ansible.builtin.raw: "{{ cli_command }}"
      register: output
      changed_when: false

    - name: Show output
      ansible.builtin.debug:
        msg: "{{ output.stdout_lines }}"
```

### Template 2: Nokia SR Linux - Show Version (Works on any EE)
```yaml
---
- name: Nokia SR Linux - Show Version
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  tasks:
    - name: Get version via raw SSH
      ansible.builtin.raw: "show version"
      register: output
      changed_when: false

    - name: Show result
      ansible.builtin.debug:
        msg: "{{ output.stdout_lines }}"
```

### Template 3: Nokia SR Linux - Version Audit with Parsing
```yaml
---
- name: Nokia SR Linux - Software Version Audit
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  tasks:
    - name: Get software version
      ansible.builtin.raw: "show version"
      register: version_raw
      changed_when: false

    - name: Show version output
      ansible.builtin.debug:
        msg: "{{ inventory_hostname }}: {{ version_raw.stdout | trim }}"
```

### Template 4: Nokia SR Linux - Configuration via SSH
```yaml
---
- name: Nokia SR Linux - Apply Configuration
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  tasks:
    - name: Apply configuration commands
      ansible.builtin.raw: |
        enter candidate
        set / {{ config_command }}
        commit now
      register: config_out
      changed_when: "'committed' in (config_out.stdout | default(''))"

    - name: Show result
      ansible.builtin.debug:
        msg: "{{ config_out.stdout_lines | default([]) }}"
```

### Template 5: Multi-vendor command execution
```yaml
---
- name: Multi-vendor show command
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  tasks:
    - name: Run vendor-specific command
      ansible.builtin.raw: "{{ show_command }}"
      register: output
      changed_when: false

    - name: Display output
      ansible.builtin.debug:
        msg: "{{ inventory_hostname }}: {{ output.stdout | trim }}"
```

### Template 6: Backup device configuration
```yaml
---
- name: Backup device configuration
  hosts: all
  gather_facts: false
  vars:
    ansible_connection: ssh
    ansible_ssh_common_args: "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
  tasks:
    - name: Get running config
      ansible.builtin.raw: "{{ backup_command | default('info /') }}"
      register: config_output
      changed_when: false

    - name: Save config locally
      ansible.builtin.copy:
        content: "{{ config_output.stdout }}"
        dest: "/tmp/backup_{{ inventory_hostname }}.cfg"
      delegate_to: localhost
```

---

## Common Errors and Solutions

### Error: `found unknown escape character`
**Cause:** Backslashes in regex not properly escaped in YAML.
**Solution:** Double-escape all backslashes (`\s` → `\\s`). Use `>-` block scalar for complex expressions.

### Error: `connection plugin 'network_cli' was not found`
**Cause:** The Execution Environment does not have `ansible.netcommon` collection installed.
**Solution:** Use `connection: ssh` with `ansible.builtin.raw` module, or create a custom EE with required collections.

### Error: `ansible_network_os is not supported`
**Cause:** The vendor collection (e.g., `nokia.srlinux`) is not installed in the EE.
**Solution:** Use `connection: ssh` + `raw` module for basic operations, or build a custom EE.

### Error: `SSH password: host_list declined parsing`
**Cause:** Inventory format issues or credential not properly configured.
**Solution:** Ensure credentials are configured in the AWX inventory variables or attached via credential object on the Job Template.

### Error: `Expecting value: line 1 column 1 (char 0)` + `found unknown escape character`
**Cause:** Combined JSON/YAML parsing failure due to escape characters in the playbook.
**Solution:** This is the regex backslash problem. Fix all `\s`, `\w`, `\d`, `\.` to `\\s`, `\\w`, `\\d`, `\\.` and use `>-` for long lines.

### Error: `template error while templating string`
**Cause:** Jinja2 syntax error, often from unbalanced braces or incorrect filter usage.
**Solution:** Check that all `{{ }}` are balanced and filters are properly chained with `|`.

### Error: `The task includes an option with an undefined variable`
**Cause:** A variable referenced in the playbook is not defined.
**Solution:** Use `| default('fallback_value')` filter for optional variables, or define them in inventory/extra_vars.

---

## AWX-Specific Playbook Guidelines

### Inventory Variables for Network Devices
Set these at the inventory level for all hosts:
```yaml
---
ansible_user: admin
ansible_password: your_password
# Do NOT set ansible_connection: network_cli unless the EE supports it
```

### Using extra_vars in Job Templates
```yaml
# Simple variables
extra_vars:
  cli_command: "show version"
  target_host: "node-l1"

# For templates with Jinja2, escape braces
extra_vars: |
  backup_dir: "/tmp/backups"
  filename: "backup_{{{{ inventory_hostname }}}}.cfg"
```

### Job Artifacts with set_stats
Use `set_stats` to pass data from playbook execution back to AWX:
```yaml
- name: Publish results as artifacts
  ansible.builtin.set_stats:
    data:
      result_key: "{{ some_value }}"
    per_host: false  # true for per-host data, false for aggregate
```

### Idempotency with raw module
The `raw` module is not idempotent by default. Always use:
```yaml
- ansible.builtin.raw: "command here"
  changed_when: false  # or a meaningful condition
```

---

## Recommended Module Usage by EE Type

### Default EE (no extra collections)
- `ansible.builtin.raw` - Execute raw commands over SSH
- `ansible.builtin.command` - Execute commands on remote (Linux targets)
- `ansible.builtin.shell` - Execute shell commands on remote (Linux targets)
- `ansible.builtin.debug` - Print messages/variables
- `ansible.builtin.set_fact` - Set host facts
- `ansible.builtin.set_stats` - Set job artifacts
- `ansible.builtin.copy` - Copy files
- `ansible.builtin.template` - Template files
- `ansible.builtin.lineinfile` - Manage lines in files
- `ansible.builtin.uri` - HTTP requests
- `ansible.builtin.wait_for` - Wait for conditions
- `ansible.builtin.pause` - Pause execution

### Custom EE with network collections
All of the above, plus:
- `ansible.netcommon.cli_command` - Run CLI commands on network devices
- `ansible.netcommon.cli_config` - Push configuration to network devices
- `nokia.srlinux.cli` - Nokia SR Linux specific modules
