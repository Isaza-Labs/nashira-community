# AWX/Ansible Tower Complete Guide

## Platform Overview

### What is AWX?
AWX is the open-source upstream project for Ansible Tower. It provides a web UI, REST API, and task engine for Ansible automation.

**Key Concepts:**
- AWX runs Ansible playbooks against managed nodes (hosts)
- Provides RBAC (Role-Based Access Control), scheduling, and audit trails
- Uses "Execution Environments" (container images) to run playbooks in isolated environments
- Projects sync playbooks from SCM (Git, SVN) repositories
- Inventories define which hosts to manage
- Job Templates combine: Project + Inventory + Playbook + Credentials + Execution Environment

### Architecture Components
1. **Web Server**: Provides the UI and REST API
2. **Task Engine**: Executes jobs using Ansible
3. **PostgreSQL Database**: Stores configuration and job history
4. **Redis**: Message broker for task coordination
5. **Execution Environments**: Container images with Ansible and dependencies

---

## Execution Environments (EE)

### Overview
- EEs are container images that contain Ansible, Python, and all dependencies
- They replace the old "virtualenvs" approach for isolated execution
- Default EE: "AWX EE (latest)" or "Default execution environment"
- Custom EEs can include specific Python packages, Ansible collections, etc.

**CRITICAL**: Jobs MUST have an Execution Environment assigned to run successfully.

### Common Execution Environments
- `AWX EE (latest)` - Default AWX execution environment
- `Control Plane Execution Environment` - For AWX internal operations
- `Minimal Execution Environment` - Lightweight, basic Ansible only
- Custom EEs - Organization-specific with required packages

---

## Credential Types

### 1. Machine (SSH)
For connecting to managed hosts via SSH:
- Username/password or SSH private key
- Privilege escalation (become) settings
- SSH port configuration

### 2. Source Control (SCM)
For accessing Git/SVN repositories:
- Username/password for HTTPS
- SSH key for SSH URLs
- Required for private repositories

### 3. Vault
For Ansible Vault encrypted content:
- Vault password
- Multiple vault IDs supported

### 4. Cloud Credentials
- **AWS**: Access Key + Secret Key
- **Azure**: Subscription ID + Client credentials
- **GCP**: Service Account JSON
- **VMware vSphere**: Username + Password + Host

### 5. Network
For network devices (Cisco, Juniper, etc.):
- Username/password
- SSH key
- Enable password for privilege mode

### 6. Container Registry
For pulling Execution Environment images:
- Registry URL
- Username/password or token

---

## Inventory Types

### 1. Static Inventory
Manually defined hosts and groups:
- Direct host entry with name/IP
- Groups for organization
- Host and group variables

### 2. Dynamic Inventory
Auto-populated from external sources:
- **Cloud providers**: AWS EC2, Azure, GCP, VMware
- **SCM**: Inventory files from Git repositories
- **Custom scripts**: Python or shell scripts
- **Satellite/Foreman**: Red Hat infrastructure

### 3. Smart Inventory
Dynamic filtering of existing inventories:
- Filter by host facts
- Filter by group membership
- Combine multiple inventories

---

## Host Connection Methods

### SSH (Default)
Standard SSH connection for Linux/Unix hosts:
- **Requirements**: SSH credentials (key or password)
- **Port**: 22 (default, configurable via `ansible_port`)
- **Python**: Must be installed on target host
- **Variables**: `ansible_host`, `ansible_user`, `ansible_ssh_private_key_file`

### WinRM
For Windows hosts:
- Uses pywinrm library
- Requires proper WinRM configuration on Windows
- Variables: `ansible_connection: winrm`, `ansible_winrm_transport`

### Local
Runs on the AWX server itself:
- **Connection**: `connection: local` in playbook
- **No SSH needed**: Runs directly on AWX container
- **Use case**: API calls, local file operations, testing

### Docker/Podman
For container management:
- Direct container execution
- Variables: `ansible_connection: docker`

---

## Variable Precedence (Lowest to Highest)

1. Role defaults (`roles/x/defaults/main.yml`)
2. Inventory file or script group vars
3. Inventory `group_vars/all`
4. Playbook `group_vars/all`
5. Inventory `group_vars/*`
6. Playbook `group_vars/*`
7. Inventory file or script host vars
8. Inventory `host_vars/*`
9. Playbook `host_vars/*`
10. Host facts / cached set_facts
11. Play vars, vars_prompt, vars_files
12. Role vars (`roles/x/vars/main.yml`)
13. Block vars (in a block/rescue/always)
14. Task vars
15. Include vars
16. Set_facts / registered vars
17. Role (and include_role) params
18. Include params
19. **Extra vars (HIGHEST PRIORITY - from AWX job template or CLI)**

---

## Troubleshooting Guide

### 1. UNREACHABLE - Host Connection Failed

**CAUSES:**
- Host is not reachable on the network
- SSH service not running on target
- Wrong SSH port configured
- Firewall blocking connection
- DNS resolution failing
- SSH credentials incorrect or missing

**SOLUTIONS:**
- Verify host is pingable: Use `awx_ping` or run ad-hoc ping module
- Check credential is assigned to job template
- Verify SSH port in host variables: `ansible_port`
- Check host variables: `ansible_host`, `ansible_user`
- For localhost: Use `connection: local` in playbook
- Ensure Python is installed on target host

### 2. Permission Denied SSH Errors

**CAUSES:**
- Wrong SSH key or password
- User doesn't exist on target
- SSH key not in authorized_keys
- SELinux/AppArmor blocking

**SOLUTIONS:**
- Verify credential has correct private key/password
- Check `ansible_user` variable matches actual user
- Ensure public key is in `~/.ssh/authorized_keys` on target
- Check file permissions: private key should be 600

### 3. No Module Named / Python Errors

**CAUSES:**
- Missing Python package in Execution Environment
- Wrong Python interpreter on target
- Missing Ansible collection

**SOLUTIONS:**
- Use correct Execution Environment with required packages
- Set `ansible_python_interpreter` in host vars
- Install required collections in EE or project requirements

### 4. Project Sync Failed

**CAUSES:**
- Invalid SCM URL
- Missing/wrong SCM credentials for private repo
- Branch doesn't exist
- Network connectivity issues

**SOLUTIONS:**
- Verify SCM URL is correct and accessible
- Assign SCM credential for private repositories
- Check branch name exists in repository
- Verify AWX can reach the Git server

### 5. Playbook Not Found

**CAUSES:**
- Project not synced
- Wrong playbook path in job template
- Playbook deleted from repository

**SOLUTIONS:**
- Sync the project first (`update_project`)
- Verify playbook file exists in repository
- Check playbook path is relative to project root

### 6. Vault Password Required

**CAUSES:**
- Playbook uses encrypted vault content
- Vault credential not assigned

**SOLUTIONS:**
- Create a Vault credential with the password
- Assign Vault credential to job template

### 7. Execution Environment Image Pull Failed

**CAUSES:**
- EE image doesn't exist
- Registry authentication required
- Network can't reach container registry

**SOLUTIONS:**
- Verify EE image name and tag
- Add Container Registry credential if needed
- Check AWX can reach the registry

### 8. Job Stuck in Pending or Waiting

**CAUSES:**
- No available capacity (all instances busy)
- Instance group has no instances
- Dependent job not completed

**SOLUTIONS:**
- Check instance capacity with `get_instances`
- Verify instance group has active instances
- Cancel stuck/waiting jobs if blocking

### 9. Localhost Unreachable When Testing

**CAUSES:**
- Trying to SSH to localhost instead of local connection
- Default inventory pointing to 127.0.0.1 with SSH

**SOLUTIONS:**
- Use `connection: local` in playbook for localhost execution
- Or set `ansible_connection: local` in inventory host vars
- Or use an inventory with properly configured hosts

---

## Important Playbook Settings for Localhost

When running playbooks on localhost (AWX server itself):

```yaml
---
- name: Run on localhost
  hosts: localhost
  connection: local  # CRITICAL - prevents SSH to self
  gather_facts: false  # Optional - speeds up execution
  tasks:
    - name: Example task
      debug:
        msg: "Running locally"
```

---

## Job Status Values

| Status | Description |
|--------|-------------|
| `new` | Job created but not started |
| `pending` | Job waiting for capacity |
| `waiting` | Job waiting for dependencies |
| `running` | Job currently executing |
| `successful` | Job completed successfully |
| `failed` | Job completed with failure |
| `error` | Job had an error (AWX/system issue) |
| `canceled` | Job was canceled by user |

---

## Best Practices

### Project Organization
- Use descriptive names: "webservers-deployment" not "project1"
- One project per application/purpose
- Use branches for environments (dev, staging, prod)
- Keep playbooks simple and focused
- Use roles for reusable automation

### Inventory Organization
- Separate inventories per environment (dev, staging, prod)
- Use groups to organize hosts by function (webservers, databases)
- Set group variables for common settings
- Use host variables for host-specific settings
- For dynamic environments, use inventory sources

### Credential Management
- Never hardcode credentials in playbooks
- Use AWX credential types for secure storage
- Limit credential access with RBAC
- Rotate credentials regularly
- Use different credentials per environment

### Job Template Best Practices
- Always assign an Execution Environment
- Use "Prompt on launch" for flexible execution
- Set appropriate verbosity for debugging (0-5)
- Use job tags for selective execution
- Enable "Allow simultaneous" only when safe

### Execution Environment Selection
- Use "AWX EE (latest)" for standard Ansible tasks
- Create custom EEs for specific requirements
- Include all required Python packages and collections
- Test EEs before using in production

---

## Common Ansible Modules Reference

### System Modules
- `ping` - Test connectivity
- `command` - Run commands (no shell features)
- `shell` - Run commands through shell
- `copy` - Copy files to remote
- `file` - Manage file attributes
- `template` - Copy templates with variables
- `user` - Manage users
- `group` - Manage groups
- `service` - Manage services
- `package/yum/apt` - Package management
- `cron` - Manage cron jobs

### Cloud Modules
- `amazon.aws.*` - AWS resources
- `azure.azcollection.*` - Azure resources
- `google.cloud.*` - GCP resources
- `community.vmware.*` - VMware vSphere

### Network Modules
- `cisco.ios.*` - Cisco IOS devices
- `junipernetworks.junos.*` - Juniper devices
- `arista.eos.*` - Arista EOS

### Utility Modules
- `debug` - Print messages/variables
- `assert` - Verify conditions
- `set_fact` - Set variables
- `pause` - Wait for input/time
- `wait_for` - Wait for conditions
- `uri` - HTTP requests

---

## RRULE Format for Schedules

AWX schedules use iCal RRULE format:

### Examples
- **Every day at 2 AM**: `DTSTART:20240101T020000Z RRULE:FREQ=DAILY;INTERVAL=1`
- **Every Monday at 9 AM**: `DTSTART:20240101T090000Z RRULE:FREQ=WEEKLY;BYDAY=MO`
- **Every hour**: `DTSTART:20240101T000000Z RRULE:FREQ=HOURLY;INTERVAL=1`
- **First day of month**: `DTSTART:20240101T000000Z RRULE:FREQ=MONTHLY;BYMONTHDAY=1`
- **Run once**: `DTSTART:20240115T140000Z RRULE:FREQ=DAILY;COUNT=1`

---

## API Error Codes

| Code | Meaning | Solution |
|------|---------|----------|
| 400 | Bad Request | Check payload format and required fields |
| 401 | Unauthorized | Verify API token/credentials |
| 403 | Forbidden | Check RBAC permissions |
| 404 | Not Found | Resource doesn't exist, verify ID |
| 405 | Method Not Allowed | Check HTTP method (GET/POST/PATCH/DELETE) |
| 409 | Conflict | Resource already exists or dependency issue |
| 500 | Server Error | AWX internal error, check logs |

---

## Workflow Execution Order

When executing workflows:
1. **Parallel nodes**: Execute simultaneously if no dependencies
2. **Success path**: Next node runs on successful completion
3. **Failure path**: Alternative node runs on failure
4. **Always path**: Node runs regardless of previous result
5. **Convergence**: Multiple paths can merge back

---

## Extra Variables Format

Extra vars in AWX can be provided as:

### YAML format (recommended)
```yaml
environment: production
debug_mode: true
servers:
  - web1
  - web2
```

### JSON format
```json
{"environment": "production", "debug_mode": true}
```

### Key-value pairs (in API)
```json
[{"key": "environment", "value": "production"}]
```

---

## Checking Job Failures Workflow

1. Always get job details with `get_job_details`
2. Get full output with `get_job_stdout`
3. Look for error messages in the output
4. Check if credential was assigned
5. Verify inventory has correct host configuration
6. Check Execution Environment is assigned
7. Review playbook syntax if "YAML parsing error"

---

## NetBox Dynamic Inventory (netbox.netbox.nb_inventory)

### Overview
NetBox is an IP address management (IPAM) and data center infrastructure management (DCIM) tool. AWX can use NetBox as a dynamic inventory source to automatically discover and manage devices.

**Collection Required**: `netbox.netbox` (must be in the Execution Environment)

### Creating NetBox Inventory Source - Complete Workflow

**RECOMMENDED METHOD: Token in source_vars (No Credential Required)**

This method embeds the NetBox API token directly in the source_vars configuration. This is the simplest approach and does NOT require a NetBox credential type to be configured in AWX.

#### Step 1: Create Inventory (if not exists)

```python
create_inventory({
    "inventory": {
        "name": "NetBox Dynamic Inventory",
        "description": "Hosts from NetBox DCIM",
        "organization": 1
    }
})
```

#### Step 2: Create Inventory Source with Token in source_vars

**CRITICAL**:
- The source must be `netbox.netbox.nb_inventory`
- The token is included directly in source_vars (no credential needed)

```python
create_inventory_source({
    "inventory_source": {
        "name": "NetBox Source",
        "description": "Dynamic inventory from NetBox",
        "source": "netbox.netbox.nb_inventory",
        "inventory": <inventory_id>,
        "source_vars": "---\nplugin: netbox.netbox.nb_inventory\napi_endpoint: https://netbox.example.com/\ntoken: <your-netbox-api-token>\nvalidate_certs: false\nquery_timeout: 400\ngroup_by:\n  - device_roles\n  - manufacturers\nquery_filters:\n  - status: active\ncompose:\n  netbox_device_id: id",
        "overwrite": true,
        "overwrite_vars": true,
        "update_on_launch": false
    }
})
```

#### Step 3: Sync and Verify

```python
# Sync the inventory source
sync_inventory_source(inventory_source_id)

# Verify hosts were imported
get_hosts(inventory=<inventory_id>)
```

### source_vars Configuration (YAML format)

The `source_vars` field contains the inventory plugin configuration in YAML. **The token is included directly in source_vars.**

#### Standard Configuration (Recommended)
```yaml
---
plugin: netbox.netbox.nb_inventory
api_endpoint: https://netbox.example.com/
token: <your-netbox-api-token>
validate_certs: false
query_timeout: 400
group_by:
  - device_roles
  - manufacturers
query_filters:
  - status: active
compose:
  netbox_device_id: id
```

#### Full Configuration with All Options
```yaml
---
plugin: netbox.netbox.nb_inventory
api_endpoint: https://netbox.example.com/
token: <your-netbox-api-token>
validate_certs: false
query_timeout: 400
config_context: false

# Group hosts by these attributes
group_by:
  - sites
  - device_roles
  - platforms
  - regions
  - tenants
  - tags
  - manufacturers
  - device_types

# Filter which devices to include
query_filters:
  - status: active
  - site: "main-datacenter"
  - role: "server"

# Compose variables from NetBox data
compose:
  netbox_device_id: id
  ansible_host: primary_ip4.address | ansible.netcommon.ipaddr('address')
  ansible_network_os: platform.slug

# Keyed groups for dynamic grouping
keyed_groups:
  - key: platforms | map(attribute='slug') | list
    prefix: platform
  - key: sites | map(attribute='slug') | list
    prefix: site
```

### source_vars Parameters Reference

| Parameter | Description | Example |
|-----------|-------------|---------|
| `plugin` | Plugin name (REQUIRED) | `netbox.netbox.nb_inventory` |
| `api_endpoint` | NetBox URL with trailing slash | `https://netbox.example.com/` |
| `token` | NetBox API token | `74655b8e47bdc4d7f71b6bee7837d4e6032c09e1` |
| `validate_certs` | SSL certificate validation | `false` for self-signed |
| `query_timeout` | API query timeout in seconds | `400` |
| `group_by` | Attributes to group hosts by | `[device_roles, manufacturers]` |
| `query_filters` | Filters for device selection | `[{status: active}]` |
| `compose` | Variable mappings | `{netbox_device_id: id}` |

### Common source_vars Examples

#### Basic Configuration (All Active Devices)
```yaml
---
plugin: netbox.netbox.nb_inventory
api_endpoint: https://netbox.example.com/
token: <your-netbox-api-token>
validate_certs: false
query_timeout: 400
group_by:
  - device_roles
  - manufacturers
query_filters:
  - status: active
compose:
  netbox_device_id: id
```

#### Network Devices Only
```yaml
---
plugin: netbox.netbox.nb_inventory
api_endpoint: https://netbox.example.com/
token: <your-netbox-api-token>
validate_certs: false
query_timeout: 400
group_by:
  - sites
  - platforms
  - device_roles
query_filters:
  - status: active
  - role__n: server
compose:
  netbox_device_id: id
  ansible_host: primary_ip4.address | ansible.netcommon.ipaddr('address')
  ansible_network_os: platform.slug
```

#### Servers from Specific Site
```yaml
---
plugin: netbox.netbox.nb_inventory
api_endpoint: https://netbox.example.com/
token: <your-netbox-api-token>
validate_certs: false
query_timeout: 400
group_by:
  - device_roles
  - tags
query_filters:
  - status: active
  - site: datacenter-01
  - role: server
compose:
  netbox_device_id: id
  ansible_host: primary_ip4.address | ansible.netcommon.ipaddr('address')
```

### Important Notes for NetBox Inventory

1. **Execution Environment**: Must include `netbox.netbox` collection
   - Check with `get_execution_environments`
   - Usually "AWX EE (latest)" includes it, or use custom EE

2. **Token in source_vars** (Recommended):
   - Include `token` directly in source_vars
   - No need to create a separate credential
   - Simpler setup, works on all AWX installations

3. **API Endpoint**:
   - Must include trailing slash: `https://netbox.example.com/`
   - Use HTTPS for production environments

4. **query_timeout**:
   - Recommended value: `400` seconds
   - Increase for large NetBox installations

5. **Sync**: After creating, sync the inventory source:
   ```python
   sync_inventory_source(inventory_source_id)
   ```

6. **Validation**: Check sync status and verify hosts were imported:
   ```python
   get_hosts(inventory=<inventory_id>)
   ```

### Alternative Method: Using AWX Credential

If your AWX installation has a NetBox credential type configured, you can use it instead of embedding the token in source_vars:

1. **Find NetBox credential type**: `get_credential_types(name="NetBox")`
2. **Create credential** with netbox_url and netbox_token inputs
3. **Link credential** to inventory source instead of putting token in source_vars

**Note**: Many AWX installations do NOT have the NetBox credential type pre-configured. The token-in-source_vars method is more universally compatible.

### Troubleshooting NetBox Inventory

| Error | Cause | Solution |
|-------|-------|----------|
| "No module named netbox" | Collection not in EE | Use EE with netbox.netbox collection |
| "401 Unauthorized" | Invalid NetBox token | Verify token in NetBox UI |
| "Connection refused" | Wrong URL or network | Check api_endpoint URL, verify connectivity |
| "No hosts found" | Query filters too restrictive | Adjust query_filters or remove them |
| "SSL certificate verify failed" | Self-signed cert | Set `validate_certs: false` in source_vars |
| "Timeout" | Large inventory or slow network | Increase `query_timeout` value |
| "NetBox credential type not found" | AWX doesn't have NetBox credential | Use token-in-source_vars method instead |

### Complete Workflow: Create NetBox Dynamic Inventory

When user asks to create a NetBox inventory, follow these steps:

1. **Get user information**:
   - NetBox URL (api_endpoint)
   - NetBox API token
   - Desired group_by options (default: device_roles, manufacturers)
   - Query filters (default: status: active)

2. **Create inventory**:
   - Call `create_inventory` with appropriate name

3. **Create inventory source with token in source_vars**:
   - Use source: `netbox.netbox.nb_inventory`
   - Configure source_vars with token, api_endpoint, and filters
   - NO credential needed when using token in source_vars

4. **Sync the inventory source**:
   - Call `sync_inventory_source`

5. **Verify**:
   - Call `get_hosts` to see imported devices
