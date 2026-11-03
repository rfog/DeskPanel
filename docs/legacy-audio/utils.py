import os
import yaml
import shutil

def load_config(config_path: str = "config.yaml") -> dict:
    """Loads and parses the YAML configuration file."""
    if not os.path.exists(config_path):
        raise FileNotFoundError(f"Configuration file not found: {config_path}")
    
    with open(config_path, 'r', encoding='utf-8') as file:
        return yaml.safe_load(file)

def get_temp_path(cfg: dict, filename: str) -> str:
    """Generates an absolute path for a temporary file based on the current working directory."""
    temp_dir = cfg.get("paths", {}).get("temp_dir", "tmp")
    
    # Resolve against the directory from which the script was executed
    base_dir = os.getcwd()
    full_temp_dir = os.path.abspath(os.path.join(base_dir, temp_dir))
    
    os.makedirs(full_temp_dir, exist_ok=True)
    return os.path.join(full_temp_dir, filename)

def get_output_path(cfg: dict, filename: str) -> str:
    """Generates an absolute path for an output file based on the current working directory."""
    out_dir = cfg.get("paths", {}).get("output_dir", "output")
    
    base_dir = os.getcwd()
    full_out_dir = os.path.abspath(os.path.join(base_dir, out_dir))
    
    os.makedirs(full_out_dir, exist_ok=True)
    return os.path.join(full_out_dir, filename)
    
def copy_to_devonthink(src_path: str, cfg: dict, dest_filename: str = None):
    """Copia el archivo al inbox de DEVONthink."""
    inbox = cfg.get("integrations", {}).get("devonthink_inbox", "")
    if not inbox:
        return
    inbox = os.path.expanduser(inbox)
    if not os.path.isdir(inbox):
        print(f"  ⚠️  DEVONthink inbox no encontrado: {inbox}")
        print(f"     Comprueba la ruta en config.yaml (integrations.devonthink_inbox)")
        return
    filename = dest_filename if dest_filename else os.path.basename(src_path)
    dest = os.path.join(inbox, filename)
    shutil.copy2(src_path, dest)
    print(f"  📥 Copiado a DEVONthink inbox: {dest}")